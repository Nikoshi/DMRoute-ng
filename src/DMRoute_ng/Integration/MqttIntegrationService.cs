using System.Buffers;
using System.Buffers.Text;
using System.Threading.Channels;
using DMRoute_ng.Gateways;
using DMRoute_ng.Routing;
using DMRoute_ng.Registry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DMRoute_ng.Integration;

public readonly struct MqttEvent(
    byte eventType,
    int srcId,
    int dstId = 0,
    bool isGroupCall = false,
    string? text = null,
    byte[]? raw = null)
{
    public readonly byte EventType = eventType; // 1 = CallActive, 2 = CallEvent, 3 = SMS, 4 = Unknown
    public readonly int SrcId = srcId;
    public readonly int DstId = dstId;
    public readonly bool IsGroupCall = isGroupCall;
    public readonly string? TextPayload = text;
    public readonly byte[]? RawPayload = raw;
}

public sealed class MqttIntegrationService : BackgroundService
{
    private readonly ILogger<MqttIntegrationService> _logger;
    private readonly MicroSubnetRouter _router;
    private readonly RepeaterRegistry _repeaterRegistry;
    private readonly int _zoneId;
    private readonly string _mqttHost;
    private readonly int _mqttPort;
    
    private readonly Channel<MqttEvent> _eventChannel;
    private RawMqttClient? _mqttClient;

    public MqttIntegrationService(
        ILogger<MqttIntegrationService> logger,
        IConfiguration config,
        MicroSubnetRouter router,
        RepeaterRegistry repeaterRegistry,
        SdsGateway  sdsGateway)
    {
        _logger = logger;
        _router = router;
        _repeaterRegistry = repeaterRegistry;
        _zoneId = config.GetValue("ZoneId", 100);
        
        _mqttHost = config.GetValue<string>("Mqtt:Host")!;
        _mqttPort = config.GetValue("Mqtt:Port", 1883);

        // Bounded Channel für Backpressure-Schutz (verhindert OutOfMemory bei Lastspitzen)
        _eventChannel = Channel.CreateBounded<MqttEvent>(new BoundedChannelOptions(1000)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        // Event-Abonnements (Allokationsfreies Schreiben in den Channel)
        _router.OnSignalingReceived += (src, dst, isGroup, dataType) =>
        {
            if (dataType == 0x01 || dataType == 0x02)
            {
                _eventChannel.Writer.TryWrite(new MqttEvent(dataType, src, dst, isGroup));
            }
        };
        
        _router.OnSignalingReceived += (src, dst, isGroup, dataType) =>
        {
            if (dataType == 0x01 || dataType == 0x02)
                _eventChannel.Writer.TryWrite(new MqttEvent(dataType, src, dst, isGroup));
        };

        _router.OnUnknownFrameReceived += (packet, src, dataType) =>
        {
            _eventChannel.Writer.TryWrite(new MqttEvent(4, src, raw: packet));
        };

        sdsGateway.OnSmsReceived += (src, text) =>
        {
            _eventChannel.Writer.TryWrite(new MqttEvent(3, src, text: text));
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Zero-Alloc Aufbau der Client-ID (dmroute_100_XXXXXXXX)
        Span<byte> tempClientId = stackalloc byte[32];
        var offset = 0;
        
        "dmroute_"u8.CopyTo(tempClientId);
        offset += 8;
        
        Utf8Formatter.TryFormat(_zoneId, tempClientId[offset..], out int writtenZone);
        offset += writtenZone;
        
        tempClientId[offset++] = (byte)'_';
        
        // Zufälligen 8-stelligen Hex-Suffix erzeugen (ersetzt die Guid-Allokation)
        var randomSuffix = (uint)Random.Shared.Next();
        Utf8Formatter.TryFormat(randomSuffix, tempClientId[offset..], out var writtenRand, new StandardFormat('X', 8));
        offset += writtenRand;

        // Exakt 1 finales Byte-Array für den MQTT-Client erzeugen
        var clientId = tempClientId[..offset].ToArray();
        _mqttClient = new RawMqttClient(clientId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await _mqttClient.ConnectAsync(_mqttHost, _mqttPort))
                {
                    _logger.LogInformation("MQTT verbunden mit {Host}:{Port}", _mqttHost, _mqttPort);
                    
                    var stateTask = Task.Run(() => StatePollingLoop(stoppingToken), stoppingToken);
                    var eventTask = Task.Run(() => EventLoop(stoppingToken), stoppingToken);

                    await Task.WhenAll(stateTask, eventTask);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("MQTT Verbindungsfehler: {Message}. Retry in 5s...", ex.Message);
                await Task.Delay(5000, stoppingToken);
            }
        }
        
        _mqttClient?.Dispose();
    }

    private async Task EventLoop(CancellationToken token)
{
    byte[] buffer = ArrayPool<byte>.Shared.Rent(1024);
    byte[] topicBuffer = ArrayPool<byte>.Shared.Rent(128);

    try
    {
        await foreach (var ev in _eventChannel.Reader.ReadAllAsync(token))
        {
            var topicSpan = topicBuffer.AsSpan();
            int topicLen = 0;
            var builder = new JsonSpanBuilder(buffer.AsSpan());

            if (ev.EventType == 0x01 || ev.EventType == 0x02)
            {
                var typeStr = ev.IsGroupCall ? "group/"u8 : "private/"u8;
                var stateStr = ev.EventType == 0x01 ? "/active"u8 : "/event"u8;
    
                int offset = 0;
                "dmroute/"u8.CopyTo(topicSpan); offset += 8;
                Utf8Formatter.TryFormat(_zoneId, topicSpan[offset..], out int w1); offset += w1;
                "/call/"u8.CopyTo(topicSpan[offset..]); offset += 6;
                typeStr.CopyTo(topicSpan[offset..]); offset += typeStr.Length;
                Utf8Formatter.TryFormat(ev.DstId, topicSpan[offset..], out int w2); offset += w2;
                stateStr.CopyTo(topicSpan[offset..]); offset += stateStr.Length;
                topicLen = offset;

                builder.AppendNumber("srcId"u8, ev.SrcId);
                builder.AppendNumber("dstId"u8, ev.DstId);
                builder.AppendString("type"u8, ev.IsGroupCall ? "GroupCall"u8 : "PrivateCall"u8); // <-- HIER WAR DER FEHLER
            }
            else if (ev.EventType == 3 && ev.TextPayload != null)
            {
                topicLen = BuildTopic(topicSpan, "sds/sms"u8);
                builder.AppendNumber("srcId"u8, ev.SrcId);
                var textBytes = System.Text.Encoding.UTF8.GetBytes(ev.TextPayload);
                builder.AppendString("message"u8, textBytes);
            }
            else if (ev.EventType == 4 && ev.RawPayload != null)
            {
                topicLen = BuildTopic(topicSpan, "diag/unknown_frame"u8);
                builder.AppendNumber("srcId"u8, ev.SrcId);
                var hexString = Convert.ToHexString(ev.RawPayload);
                builder.AppendString("hexDump"u8, System.Text.Encoding.ASCII.GetBytes(hexString));
            }

            builder.Finish();
            _mqttClient?.Publish(topicSpan[..topicLen], buffer.AsSpan(0, builder.Length), retain: false);
        }
    }
    finally
    {
        ArrayPool<byte>.Shared.Return(buffer);
        ArrayPool<byte>.Shared.Return(topicBuffer);
    }
}

    private async Task StatePollingLoop(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        byte[] buffer = ArrayPool<byte>.Shared.Rent(2048);
        byte[] topicBuffer = ArrayPool<byte>.Shared.Rent(128);

        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                var topicSpan = topicBuffer.AsSpan();
                var topicLen = BuildTopic(topicSpan, "sys/info"u8);

                var builder = new JsonSpanBuilder(buffer.AsSpan());
                builder.AppendNumber("zoneId"u8, _zoneId);
                builder.AppendNumber("activeHotspots"u8, _repeaterRegistry.GetAll().Count(x => x.Value.State == Types.RepeaterState.LoggedIn));
                builder.Finish();

                _mqttClient?.Publish(topicSpan[..topicLen], buffer.AsSpan(0, builder.Length), retain: true);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            ArrayPool<byte>.Shared.Return(topicBuffer);
        }
    }

    private int BuildTopic(Span<byte> buffer, ReadOnlySpan<byte> suffix)
    {
        int offset = 0;
        "dmroute/"u8.CopyTo(buffer); offset += 8;
        Utf8Formatter.TryFormat(_zoneId, buffer[offset..], out int w); offset += w;
        buffer[offset++] = (byte)'/';
        suffix.CopyTo(buffer[offset..]); offset += suffix.Length;
        return offset;
    }
}