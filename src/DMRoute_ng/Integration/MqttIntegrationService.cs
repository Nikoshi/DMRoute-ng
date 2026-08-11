using System.Buffers;
using System.Buffers.Text;
using System.Threading.Channels;
using DMRoute_ng.Gateways;
using DMRoute_ng.Routing;
using DMRoute_ng.Registry;
using DMRoute_ng.Types;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using static System.Text.Encoding;
using Convert = System.Convert;

namespace DMRoute_ng.Integration;

public readonly struct MqttEvent(
    byte eventType,
    int srcId,
    int dstId = 0,
    bool isGroupCall = false,
    string? text = null,
    byte[]? raw = null)
{
    // EventTypes: 1=CallActive, 2=CallEvent, 3=SMS, 4=Unknown, 5=APRS
    public readonly byte EventType = eventType;
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
    private readonly MasterRegistry _masterRegistry;
    private readonly RoamingRegistry _roamingRegistry;
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
        MasterRegistry masterRegistry,
        RoamingRegistry roamingRegistry,
        SdsGateway sdsGateway)
    {
        _logger = logger;
        _router = router;
        _repeaterRegistry = repeaterRegistry;
        _masterRegistry = masterRegistry;
        _roamingRegistry = roamingRegistry;
        _zoneId = config.GetValue("ZoneId", 100);

        _mqttHost = config.GetValue<string>("Mqtt:Host")!;
        _mqttPort = config.GetValue("Mqtt:Port", 1883);

        _eventChannel = Channel.CreateBounded<MqttEvent>(new BoundedChannelOptions(1000)
        {
            SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest
        });

        _router.OnSignalingReceived += (src, dst, isGroup, dataType) =>
        {
            if (dataType is 0x01 or 0x02 or 0xFE)
                _eventChannel.Writer.TryWrite(new MqttEvent(dataType, src, dst, isGroup));
        };

        _router.OnUnknownFrameReceived += (packet, src, _) =>
        {
            _eventChannel.Writer.TryWrite(new MqttEvent(4, src, raw: packet));
        };

        _router.OnAprsReceived += (src, packet) =>
        {
            _eventChannel.Writer.TryWrite(new MqttEvent(5, src, raw: packet));
        };

        sdsGateway.OnSmsReceived += (src, dst, text) =>
        {
            _eventChannel.Writer.TryWrite(new MqttEvent(3, src, dst, text: text));
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Span<byte> tempClientId = stackalloc byte[32];
        var offset = 0;

        "dmroute_"u8.CopyTo(tempClientId);
        offset += 8;
        Utf8Formatter.TryFormat(_zoneId, tempClientId[offset..], out int writtenZone);
        offset += writtenZone;
        tempClientId[offset++] = (byte)'_';

        var randomSuffix = (uint)Random.Shared.Next();
        Utf8Formatter.TryFormat(randomSuffix, tempClientId[offset..], out var writtenRand, new StandardFormat('X', 8));
        offset += writtenRand;

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

                if (ev.EventType == 0x01 || ev.EventType == 0x02 || ev.EventType == 0xFE)
                {
                    var typeStr = ev.IsGroupCall ? "group/"u8 : "private/"u8;
                    var stateStr = ev.EventType == 0x01 ? "/active"u8 : "/event"u8;

                    int offset = 0;
                    "dmroute/"u8.CopyTo(topicSpan);
                    offset += 8;
                    Utf8Formatter.TryFormat(_zoneId, topicSpan[offset..], out int w1);
                    offset += w1;
                    "/call/"u8.CopyTo(topicSpan[offset..]);
                    offset += 6;
                    typeStr.CopyTo(topicSpan[offset..]);
                    offset += typeStr.Length;
                    Utf8Formatter.TryFormat(ev.DstId, topicSpan[offset..], out int w2);
                    offset += w2;
                    stateStr.CopyTo(topicSpan[offset..]);
                    offset += stateStr.Length;
                    topicLen = offset;

                    builder.AppendNumber("srcId"u8, ev.SrcId);
                    builder.AppendNumber("dstId"u8, ev.DstId);
                    builder.AppendString("type"u8, ev.IsGroupCall ? "GroupCall"u8 : "PrivateCall"u8);

                    switch (ev.EventType)
                    {
                        // Neu: Status-Grund mitsenden
                        case 0x02:
                            builder.AppendString("endReason"u8, "clean"u8);
                            break;
                        case 0xFE:
                            builder.AppendString("endReason"u8, "timeout"u8);
                            break;
                    }
                }
                else if (ev is { EventType: 3, TextPayload: not null })
                {
                    topicLen = BuildTopic(topicSpan, "sds/sms"u8);
                    builder.AppendNumber("srcId"u8, ev.SrcId);
                    builder.AppendNumber("dstId"u8, ev.DstId);
                    builder.AppendString("message"u8, ev.TextPayload);
                }
                else if (ev.EventType == 4 && ev.RawPayload != null)
                {
                    topicLen = BuildTopic(topicSpan, "diag/unknown_frame"u8);
                    builder.AppendNumber("srcId"u8, ev.SrcId);
                    var hexString = Convert.ToHexString(ev.RawPayload);
                    builder.AppendString("hexDump"u8, hexString);
                }
                else if (ev.EventType == 5 && ev.RawPayload != null)
                {
                    topicLen = BuildTopic(topicSpan, "sds/aprs"u8);
                    builder.AppendNumber("srcId"u8, ev.SrcId);
                    var hexString = Convert.ToHexString(ev.RawPayload);
                    builder.AppendString("hexDump"u8, hexString);
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
        byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);
        byte[] topicBuffer = ArrayPool<byte>.Shared.Rent(128);

        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                var topicSpan = topicBuffer.AsSpan();
                long currentTicks = DateTime.UtcNow.Ticks;

                // 1. Sys Info
                var tLen = BuildTopic(topicSpan, "sys/info"u8);
                var b = new JsonSpanBuilder(buffer.AsSpan());
                b.AppendNumber("zoneId"u8, _zoneId);
                b.AppendNumber("activeHotspots"u8,
                    _repeaterRegistry.GetAll().Count(x => x.Value.State == Types.RepeaterState.LoggedIn));
                b.Finish();
                _mqttClient?.Publish(topicSpan[..tLen], buffer.AsSpan(0, b.Length), retain: true);

                // 2. Hotspots
                // 2. Hotspots (routing/hotspots)
                tLen = BuildTopic(topicSpan, "routing/hotspots"u8);
                b = new JsonSpanBuilder(buffer.AsSpan(), isArrayRoot: true);

                foreach (var kvp in _repeaterRegistry.GetAll())
                {
                    var r = kvp.Value;
                    if (r.State != RepeaterState.LoggedIn) continue;

                    b.StartArrayObject();
                    b.AppendNumber("id"u8, r.Id);

                    var cfg = r.Configuration;
                    if (cfg != null)
                    {
                        b.AppendString("callsign"u8, UTF8.GetBytes(cfg.Callsign));
                        b.AppendString("rxFreq"u8, UTF8.GetBytes(cfg.RxFreq));
                        b.AppendString("txFreq"u8, UTF8.GetBytes(cfg.TxFreq));
                        b.AppendNumber("txPower"u8, cfg.TxPower);
                        b.AppendNumber("colorCode"u8, cfg.ColorCode);
                        b.AppendString("location"u8, UTF8.GetBytes(cfg.Location));
                        b.AppendString("description"u8, UTF8.GetBytes(cfg.Description));
                        b.AppendString("software"u8, UTF8.GetBytes(cfg.SoftwareId));
                        b.AppendString("package"u8, UTF8.GetBytes(cfg.PackageId));
                    }
                    else
                    {
                        b.AppendString("callsign"u8, "N/A"u8);
                    }

                    b.AppendNumber("lastPingSecAgo"u8, (currentTicks - r.LastPingTicks) / TimeSpan.TicksPerSecond);
                    b.EndArrayObject();
                }

                b.Finish();
                _mqttClient?.Publish(topicSpan[..tLen], buffer.AsSpan(0, b.Length), retain: true);

                // 3. Mesh Peers
                tLen = BuildTopic(topicSpan, "routing/mesh/peers"u8);
                b = new JsonSpanBuilder(buffer.AsSpan(), isArrayRoot: true);
                foreach (var kvp in _masterRegistry.GetAll())
                {
                    b.StartArrayObject();
                    b.AppendNumber("zoneId"u8, kvp.Value.ZoneId);
                    b.AppendString("endpoint"u8, kvp.Value.DataEndPoint.ToString());
                    b.AppendNumber("lastSeenSecAgo"u8,
                        (currentTicks - kvp.Value.LastSeenTicks) / TimeSpan.TicksPerSecond);
                    b.EndArrayObject();
                }

                b.Finish();
                _mqttClient?.Publish(topicSpan[..tLen], buffer.AsSpan(0, b.Length), retain: true);
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
        "dmroute/"u8.CopyTo(buffer);
        offset += 8;
        Utf8Formatter.TryFormat(_zoneId, buffer[offset..], out int w);
        offset += w;
        buffer[offset++] = (byte)'/';
        suffix.CopyTo(buffer[offset..]);
        offset += suffix.Length;
        return offset;
    }
}