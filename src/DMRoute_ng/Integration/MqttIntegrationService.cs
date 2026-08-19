using System.Buffers;
using System.Buffers.Text;
using System.Threading.Channels;
using DMRoute_ng.Gateways;
using DMRoute_ng.Registry;
using DMRoute_ng.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DMRoute_ng.Integration;

// Event-Struktur (unverändert, sauber mit DurationSec für History)
public readonly struct MqttEvent(
    byte eventType,
    int srcId,
    int dstId = 0,
    bool isGroupCall = false,
    string? text = null,
    byte[]? raw = null,
    int durationSec = 0)
{
    public readonly byte EventType = eventType;
    public readonly int SrcId = srcId;
    public readonly int DstId = dstId;
    public readonly bool IsGroupCall = isGroupCall;
    public readonly string? TextPayload = text;
    public readonly byte[]? RawPayload = raw;
    public readonly int DurationSec = durationSec;
}

public sealed class MqttIntegrationService : BackgroundService
{
    private readonly ILogger<MqttIntegrationService> _logger;
    private readonly MasterRegistry _masterRegistry;
    private readonly int _zoneId;
    
    // Nutzt jetzt DIREKT deinen RawMqttClient
    private readonly RawMqttClient _mqttClient; 
    private readonly Channel<MqttEvent> _eventChannel;
    private string _mqttHost;
    private int _mqttPort;

    public MqttIntegrationService(
        ILogger<MqttIntegrationService> logger,
        MicroSubnetRouter router,
        SdsGateway sdsGateway,
        MasterRegistry masterRegistry,
        IConfiguration config,
        RawMqttClient mqttClient,
        Channel<MqttEvent> eventChannel)
    {
        _logger = logger;
        _masterRegistry = masterRegistry;
        _zoneId = config.GetValue("ZoneId", 100); // TODO: Das sollte hier nicht doppelt stehen :) wir übergeben die lieber über den Constructor
        _mqttClient = mqttClient;
        _eventChannel = eventChannel;

        _mqttHost = config.GetValue<string>("Mqtt:Host")!;
        _mqttPort = config.GetValue("Mqtt:Port", 1883);
        
        router.OnSignalingReceived += (src, dst, isGroup, dataType, durationSec) =>
        {
            if (dataType is 0x01 or 0x02 or 0xFE)
            {
                // EventType 1 (Active) bei 0x01, sonst 2 (End)
                var eventType = dataType == 0x01 ? (byte)1 : (byte)2;
                _eventChannel.Writer.TryWrite(new MqttEvent(eventType, src, dst, isGroup, durationSec: durationSec));
            }
        };

        // SMS Events
        sdsGateway.OnSmsReceived += (src, dst, text) =>
        {
            _eventChannel.Writer.TryWrite(new MqttEvent(3, src, dst, text: text));
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await _mqttClient.ConnectAsync(_mqttHost, _mqttPort))
                {
                    _logger.LogInformation("MQTT verbunden mit {Host}:{Port}", _mqttHost, _mqttPort);
                
                    // Event-Loop starten, solange die Verbindung steht
                    await EventLoop(stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("MQTT Verbindungsfehler: {Message}. Retry in 5s...", ex.Message);
                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    private async Task EventLoop(CancellationToken token)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);
        byte[] topicBuffer = ArrayPool<byte>.Shared.Rent(256);

        try
        {
            await foreach (var mqttEvent in _eventChannel.Reader.ReadAllAsync(token))
            {
                var bufferSpan = buffer.AsSpan();
                var topicSpan = topicBuffer.AsSpan();

                switch (mqttEvent.EventType)
                {
                    case 0x01:
                        PublishCallActive(mqttEvent, topicSpan, bufferSpan);
                        break;
                    case 0x02:
                        PublishCallEnd(mqttEvent, topicSpan, bufferSpan);
                        break;
                    case 0x03:
                        PublishSms(mqttEvent, topicSpan, bufferSpan);
                        break;
                    case 0x10:
                        PublishGuest(mqttEvent, topicSpan, bufferSpan, true);
                        break;
                    case 0x11:
                        PublishGuest(mqttEvent, topicSpan, bufferSpan, false);
                        break;
                    case 0x12:
                        PublishAway(mqttEvent, topicSpan, bufferSpan, true);
                        break;
                    case 0x13:
                        PublishAway(mqttEvent, topicSpan, bufferSpan, false);
                        break;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            ArrayPool<byte>.Shared.Return(topicBuffer);
        }
    }

    private void PublishCallActive(in MqttEvent ev, Span<byte> topicSpan, Span<byte> bufferSpan)
    {
        int tLen = BuildBaseTopic(topicSpan, "calls/active/"u8);
        Utf8Formatter.TryFormat(ev.DstId, topicSpan[tLen..], out int w);
        tLen += w;
        topicSpan[tLen++] = (byte)'/';
        Utf8Formatter.TryFormat(ev.SrcId, topicSpan[tLen..], out w);
        tLen += w;

        // Dein JsonSpanBuilder: isArrayRoot = false schreibt automatisch '{'
        var b = new JsonSpanBuilder(bufferSpan, isArrayRoot: false);
        b.AppendString("type"u8, ev.IsGroupCall ? "GroupCall"u8 : "PrivateCall"u8);
        b.AppendNumber("startTime"u8, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        b.Finish(); // Schreibt '}'

        // Dein RawMqttClient frisst direkt ReadOnlySpan<byte>
        _mqttClient.Publish(topicSpan[..tLen], bufferSpan[..b.Length], retain: true);
    }

    private void PublishCallEnd(in MqttEvent ev, Span<byte> topicSpan, Span<byte> bufferSpan)
    {
        // 1. Aktiven Call löschen (Leeres Topic + Retain)
        int tLen = BuildBaseTopic(topicSpan, "calls/active/"u8);
        Utf8Formatter.TryFormat(ev.DstId, topicSpan[tLen..], out int w);
        tLen += w;
        topicSpan[tLen++] = (byte)'/';
        Utf8Formatter.TryFormat(ev.SrcId, topicSpan[tLen..], out w);
        tLen += w;

        // RawMqttClient mit leerem Span -> Topic verschwindet aus dem Broker
        _mqttClient.Publish(topicSpan[..tLen], ReadOnlySpan<byte>.Empty, retain: true);

        // 2. Historie schreiben
        tLen = BuildBaseTopic(topicSpan, "calls/history/"u8);
        Utf8Formatter.TryFormat(ev.DstId, topicSpan[tLen..], out w);
        tLen += w;
        topicSpan[tLen++] = (byte)'/';
        Utf8Formatter.TryFormat(ev.SrcId, topicSpan[tLen..], out w);
        tLen += w;

        var b = new JsonSpanBuilder(bufferSpan, isArrayRoot: false);
        b.AppendString("type"u8, ev.IsGroupCall ? "GroupCall"u8 : "PrivateCall"u8);
        b.AppendNumber("durationSec"u8, ev.DurationSec);
        b.AppendNumber("timestamp"u8, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        b.Finish();

        _mqttClient.Publish(topicSpan[..tLen], bufferSpan[..b.Length], retain: false);
    }

    private void PublishSms(in MqttEvent ev, Span<byte> topicSpan, Span<byte> bufferSpan)
    {
        int tLen = BuildBaseTopic(topicSpan, "sms/"u8);
        Utf8Formatter.TryFormat(ev.DstId, topicSpan[tLen..], out int w);
        tLen += w;
        topicSpan[tLen++] = (byte)'/';
        Utf8Formatter.TryFormat(ev.SrcId, topicSpan[tLen..], out w);
        tLen += w;

        var b = new JsonSpanBuilder(bufferSpan, isArrayRoot: false);
        
        if (!string.IsNullOrEmpty(ev.TextPayload))
        {
            b.AppendString("text"u8, ev.TextPayload);
        }
        b.AppendNumber("timestamp"u8, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        b.Finish();

        _mqttClient.Publish(topicSpan[..tLen], bufferSpan[..b.Length], retain: true);
    }

    private void PublishGuest(in MqttEvent ev, Span<byte> topicSpan, Span<byte> bufferSpan, bool active)
    {
        int tLen = BuildBaseTopic(topicSpan, "roaming/guests/"u8);
        Utf8Formatter.TryFormat(ev.SrcId, topicSpan[tLen..], out int w);
        tLen += w;

        if (!active)
        {
            _mqttClient.Publish(topicSpan[..tLen], ReadOnlySpan<byte>.Empty, retain: true);
            return;
        }

        var b = new JsonSpanBuilder(bufferSpan, isArrayRoot: false);
        b.AppendNumber("timestamp"u8, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        b.Finish();

        _mqttClient.Publish(topicSpan[..tLen], bufferSpan[..b.Length], retain: true);
    }

    private void PublishAway(in MqttEvent ev, Span<byte> topicSpan, Span<byte> bufferSpan, bool active)
    {
        int tLen = BuildBaseTopic(topicSpan, "roaming/away/"u8);
        Utf8Formatter.TryFormat(ev.SrcId, topicSpan[tLen..], out int w);
        tLen += w;

        if (!active)
        {
            _mqttClient.Publish(topicSpan[..tLen], ReadOnlySpan<byte>.Empty, retain: true);
            return;
        }

        var b = new JsonSpanBuilder(bufferSpan, isArrayRoot: false);
        b.AppendNumber("foreignZone"u8, ev.DstId);
        b.AppendNumber("timestamp"u8, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        b.Finish();

        _mqttClient.Publish(topicSpan[..tLen], bufferSpan[..b.Length], retain: true);
    }
    
    private int BuildBaseTopic(Span<byte> buffer, ReadOnlySpan<byte> subTree)
    {
        int offset = 0;
        "dmroute/"u8.CopyTo(buffer);
        offset += 8;
        Utf8Formatter.TryFormat(_zoneId, buffer[offset..], out int w);
        offset += w;
        buffer[offset++] = (byte)'/';
        subTree.CopyTo(buffer[offset..]);
        offset += subTree.Length;
        return offset;
    }
}