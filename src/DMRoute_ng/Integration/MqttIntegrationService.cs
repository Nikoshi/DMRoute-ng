using System.Buffers.Text;
using System.Reflection;
using System.Text;
using System.Threading.Channels;
using DMRoute_ng.Configuration;
using DMRoute_ng.Gateways;
using DMRoute_ng.Registry;
using DMRoute_ng.Routing;
using DMRoute_ng.Services;
using DMRoute_ng.Types;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DMRoute_ng.Integration;

/// <summary>
/// Überführt begrenzte Routing- und Gateway-Ereignisse in kanonische MQTT-Themen und
/// veröffentlicht periodische Zustands-Snapshots.
/// </summary>
/// <remarks>
/// Empfangsnahe Rückrufe stellen nur kompakte Ereignisse in die begrenzte Queue ein.
/// Serialisierung, Netzwerkzugriff und alle zulässigen Allokationen erfolgen im
/// Hintergrunddienst.
/// </remarks>
public sealed partial class MqttIntegrationService : BackgroundService
{
    private const int TopicBufferBytes = 256;
    private const string RedactedLocationMessage = "[GPS position redacted]";
    private readonly ILogger<MqttIntegrationService> _logger;
    private readonly MicroSubnetRouter _router;
    private readonly SdsGateway _sdsGateway;
    private readonly RepeaterRegistry _repeaterRegistry;
    private readonly MasterRegistry _masterRegistry;
    private readonly RoamingRegistry _roamingRegistry;
    private readonly RawMqttClient _mqttClient;
    private readonly MqttTelemetryQueue _events;
    private readonly object _publishLock = new();
    private readonly string _mqttHost;
    private readonly int _mqttPort;
    private readonly int _zoneId;
    private readonly bool _locationPrivacyEnabled;
    private readonly double _locationGridKilometers;
    private readonly TimeSpan _stateInterval;
    private readonly byte[] _eventPayloadBuffer;
    private readonly byte[] _eventTopicBuffer = new byte[TopicBufferBytes];
    private readonly byte[] _statePayloadBuffer;
    private readonly byte[] _stateTopicBuffer = new byte[TopicBufferBytes];
    private readonly LocalGuestSnapshot[] _guestSnapshots;
    private readonly AwayDeviceSnapshot[] _awaySnapshots;
    private readonly ActiveCallSnapshot[] _callSnapshots;
    private HashSet<int> _publishedHotspots;
    private HashSet<int> _currentHotspots;
    private HashSet<int> _publishedPeers;
    private HashSet<int> _currentPeers;
    private HashSet<int> _publishedGuests;
    private HashSet<int> _currentGuests;
    private HashSet<int> _publishedAway;
    private HashSet<int> _currentAway;
    private HashSet<int> _publishedCalls;
    private HashSet<int> _currentCalls;
    private readonly byte[] _version;
    private readonly long _startedAtTicks = DateTime.UtcNow.Ticks;
    private long _serializationDrops;

    public MqttIntegrationService(
        ILogger<MqttIntegrationService> logger,
        MicroSubnetRouter router,
        SdsGateway sdsGateway,
        RepeaterRegistry repeaterRegistry,
        MasterRegistry masterRegistry,
        RoamingRegistry roamingRegistry,
        DmRouteSettings settings,
        RawMqttClient mqttClient)
    {
        _logger = logger;
        _router = router;
        _sdsGateway = sdsGateway;
        _repeaterRegistry = repeaterRegistry;
        _masterRegistry = masterRegistry;
        _roamingRegistry = roamingRegistry;
        _mqttClient = mqttClient;
        settings.Validate();
        _zoneId = settings.ZoneId;
        _mqttHost = settings.Mqtt.Host;
        _mqttPort = settings.Mqtt.Port;
        _locationPrivacyEnabled = settings.Mqtt.LocationPrivacyEnabled;
        _locationGridKilometers = settings.Mqtt.LocationGridKm;
        var stateIntervalSeconds = settings.Mqtt.StateIntervalSeconds;
        var eventCapacity = settings.Mqtt.EventCapacity;
        var diagnosticCapacity = settings.Mqtt.DiagnosticFrameCapacity;
        var diagnosticFrameBytes = settings.Mqtt.MaxDiagnosticFrameBytes;
        var payloadBufferBytes = settings.Mqtt.PayloadBufferBytes;

        _stateInterval = TimeSpan.FromSeconds(stateIntervalSeconds);
        _events = new MqttTelemetryQueue(eventCapacity, diagnosticCapacity, diagnosticFrameBytes,
            settings.Sds.MaxMessageBytes);
        _eventPayloadBuffer = GC.AllocateUninitializedArray<byte>(payloadBufferBytes, pinned: true);
        _statePayloadBuffer = GC.AllocateUninitializedArray<byte>(payloadBufferBytes, pinned: true);
        _guestSnapshots = new LocalGuestSnapshot[roamingRegistry.MaxLocalGuests];
        _awaySnapshots = new AwayDeviceSnapshot[roamingRegistry.MaxAwayDevices];
        _callSnapshots = new ActiveCallSnapshot[router.MaxActiveCalls];

        var stateCapacity = Math.Max(16, roamingRegistry.MaxLocalGuests);
        _publishedHotspots = new HashSet<int>(stateCapacity);
        _currentHotspots = new HashSet<int>(stateCapacity);
        _publishedPeers = new HashSet<int>(stateCapacity);
        _currentPeers = new HashSet<int>(stateCapacity);
        _publishedGuests = new HashSet<int>(stateCapacity);
        _currentGuests = new HashSet<int>(stateCapacity);
        _publishedAway = new HashSet<int>(stateCapacity);
        _currentAway = new HashSet<int>(stateCapacity);
        _publishedCalls = new HashSet<int>(Math.Max(16, router.MaxActiveCalls));
        _currentCalls = new HashSet<int>(Math.Max(16, router.MaxActiveCalls));

        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
        _version = System.Text.Encoding.UTF8.GetBytes(version);

        router.OnCallEvent += HandleCallEvent;
        router.OnUnknownFrameReceived += HandleUnknownFrame;
        sdsGateway.OnMessageReceived += HandleSms;
        sdsGateway.OnLocationReceived += HandleLocation;
    }

    public long DroppedEvents => _events.DroppedEvents + Interlocked.Read(ref _serializationDrops);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await _mqttClient.ConnectAsync(_mqttHost, _mqttPort).ConfigureAwait(false))
                    throw new IOException("Broker rejected MQTT CONNECT.");

                _logger.LogInformation("MQTT connected to {Host}:{Port}", _mqttHost, _mqttPort);
                lock (_publishLock) PublishStateSnapshot();

                using var session = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var eventLoop = RunEventLoop(_events.EventReader, session.Token);
                var diagnosticLoop = RunEventLoop(_events.DiagnosticReader, session.Token);
                var stateLoop = RunStateLoop(session.Token);
                var completed = await Task.WhenAny(eventLoop, diagnosticLoop, stateLoop).ConfigureAwait(false);
                try
                {
                    await completed.ConfigureAwait(false);
                }
                finally
                {
                    session.Cancel();
                    await IgnoreCompletion(eventLoop).ConfigureAwait(false);
                    await IgnoreCompletion(diagnosticLoop).ConfigureAwait(false);
                    await IgnoreCompletion(stateLoop).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MQTT connection failed; retrying in 5 seconds");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private async Task RunEventLoop(ChannelReader<MqttTelemetryEvent> reader, CancellationToken token)
    {
        await foreach (var item in reader.ReadAllAsync(token).ConfigureAwait(false))
        {
            try
            {
                lock (_publishLock) PublishEvent(item);
            }
            catch (ArgumentException ex)
            {
                Interlocked.Increment(ref _serializationDrops);
                _logger.LogWarning(ex, "MQTT event exceeded a configured packet buffer and was dropped");
            }
            finally
            {
                // SMS- und Diagnosepuffer gehören der Queue und werden erst nach
                // abgeschlossener Serialisierung wieder für Produzenten freigegeben.
                MqttTelemetryQueue.Release(item);
            }
        }
    }

    private async Task RunStateLoop(CancellationToken token)
    {
        using var timer = new PeriodicTimer(_stateInterval);
        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            try
            {
                lock (_publishLock) PublishStateSnapshot();
            }
            catch (ArgumentException ex)
            {
                Interlocked.Increment(ref _serializationDrops);
                _logger.LogWarning(ex, "MQTT state payload exceeded a configured packet buffer");
            }
        }
    }

    private void HandleCallEvent(in DmrCallEvent call) => _events.EnqueueCall(call);

    private void HandleUnknownFrame(
        ReadOnlySpan<byte> packet, int sourceId, int hotspotId, byte dataType, long occurredAtTicks) =>
        _events.EnqueueDiagnostic(packet, sourceId, hotspotId, dataType, occurredAtTicks);

    private void HandleSms(in SdsMessageEvent message, ReadOnlySpan<byte> textBytes)
    {
        if (DmrServiceIds.IsReserved(message.DestinationId)) return;
        _events.EnqueueSms(in message, textBytes);
    }

    private void HandleLocation(in DmrLocationEvent location) => _events.EnqueueLocation(location);

    private static ReadOnlySpan<byte> GetRepeaterState(RepeaterState state) => state switch
    {
        RepeaterState.LoggedIn => "LoggedIn"u8,
        RepeaterState.ChallengeSent => "ChallengeSent"u8,
        _ => "Disconnected"u8
    };

    private static void Swap<T>(ref T left, ref T right) => (left, right) = (right, left);

    private static async Task IgnoreCompletion(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    public override void Dispose()
    {
        _router.OnCallEvent -= HandleCallEvent;
        _router.OnUnknownFrameReceived -= HandleUnknownFrame;
        _sdsGateway.OnMessageReceived -= HandleSms;
        _sdsGateway.OnLocationReceived -= HandleLocation;
        base.Dispose();
    }
}
