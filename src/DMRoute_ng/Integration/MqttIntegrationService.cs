using System.Buffers.Text;
using System.Reflection;
using System.Threading.Channels;
using DMRoute_ng.Gateways;
using DMRoute_ng.Registry;
using DMRoute_ng.Routing;
using DMRoute_ng.Types;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DMRoute_ng.Integration;

public sealed class MqttIntegrationService : BackgroundService
{
    private const int TopicBufferBytes = 256;
    private const double MaxLocationGridKilometers = 1000d;
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
        IConfiguration config,
        RawMqttClient mqttClient)
    {
        _logger = logger;
        _router = router;
        _sdsGateway = sdsGateway;
        _repeaterRegistry = repeaterRegistry;
        _masterRegistry = masterRegistry;
        _roamingRegistry = roamingRegistry;
        _mqttClient = mqttClient;
        _zoneId = config.GetValue("ZoneId", 100);
        _mqttHost = config.GetValue<string>("Mqtt:Host") ??
                    throw new InvalidOperationException("Mqtt:Host is required.");
        _mqttPort = config.GetValue("Mqtt:Port", 1883);
        _locationPrivacyEnabled = config.GetValue("Mqtt:LocationPrivacyEnabled", true);
        _locationGridKilometers = config.GetValue("Mqtt:LocationGridKm", 10d);
        var stateIntervalSeconds = config.GetValue("Mqtt:StateIntervalSeconds", 10);
        var eventCapacity = config.GetValue("Mqtt:EventCapacity", 1000);
        var diagnosticCapacity = config.GetValue("Mqtt:DiagnosticFrameCapacity", 128);
        var diagnosticFrameBytes = config.GetValue("Mqtt:MaxDiagnosticFrameBytes", 1024);
        var payloadBufferBytes = config.GetValue("Mqtt:PayloadBufferBytes", 32768);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_mqttPort);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stateIntervalSeconds);
        ArgumentOutOfRangeException.ThrowIfLessThan(payloadBufferBytes, 32768);
        if (!double.IsFinite(_locationGridKilometers) ||
            _locationGridKilometers is <= 0d or > MaxLocationGridKilometers)
            throw new ArgumentOutOfRangeException("Mqtt:LocationGridKm",
                $"Location grid size must be finite, positive, and at most {MaxLocationGridKilometers} km.");

        _stateInterval = TimeSpan.FromSeconds(stateIntervalSeconds);
        _events = new MqttTelemetryQueue(eventCapacity, diagnosticCapacity, diagnosticFrameBytes);
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
        sdsGateway.OnSmsReceived += HandleSms;
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

    private void HandleSms(int sourceId, int destinationId, string message, bool containsLocation) =>
        _events.EnqueueSms(sourceId, destinationId,
            containsLocation && _locationPrivacyEnabled ? RedactedLocationMessage : message,
            DateTime.UtcNow.Ticks);

    private void HandleLocation(in DmrLocationEvent location) => _events.EnqueueLocation(location);

    private void PublishEvent(in MqttTelemetryEvent item)
    {
        switch (item.EventType)
        {
            case MqttTelemetryEventType.Call:
                if (item.Call.EventType == DmrCallEventType.Started) PublishCallStarted(item.Call);
                else PublishCallEnded(item.Call);
                break;
            case MqttTelemetryEventType.Sms:
                PublishSms(item);
                break;
            case MqttTelemetryEventType.Location:
                PublishLocation(item.Location);
                break;
            case MqttTelemetryEventType.DiagnosticFrame:
                PublishDiagnostic(item);
                break;
        }
    }

    private void PublishCallStarted(in DmrCallEvent call)
    {
        var payload = BuildCallPayload(call, includeEnd: false, _eventPayloadBuffer);
        _mqttClient.Publish(BuildTopic(_eventTopicBuffer, "call/active"u8), payload, retain: false);
        _mqttClient.Publish(BuildEntityTopic(_eventTopicBuffer, "call/active/"u8, call.SourceId), payload, retain: true);
    }

    private void PublishCallEnded(in DmrCallEvent call)
    {
        _mqttClient.Publish(BuildEntityTopic(_eventTopicBuffer, "call/active/"u8, call.SourceId),
            ReadOnlySpan<byte>.Empty, retain: true);
        var payload = BuildCallPayload(call, includeEnd: true, _eventPayloadBuffer);
        _mqttClient.Publish(BuildTopic(_eventTopicBuffer, "call/event"u8), payload, retain: false);
    }

    private void PublishSms(in MqttTelemetryEvent item)
    {
        var builder = new JsonSpanBuilder(_eventPayloadBuffer);
        builder.AppendNumber("srcId"u8, item.SourceId);
        builder.AppendNumber("dstId"u8, item.DestinationId);
        builder.AppendString("message"u8, item.Message);
        builder.AppendTimestamp("timestamp"u8, item.OccurredAtTicks);
        builder.Finish();
        _mqttClient.Publish(BuildTopic(_eventTopicBuffer, "sds/sms"u8),
            _eventPayloadBuffer.AsSpan(0, builder.Length), retain: false);
    }

    private void PublishDiagnostic(in MqttTelemetryEvent item)
    {
        var slot = item.DiagnosticFrame!;
        Span<byte> type = stackalloc byte[4];
        type[0] = (byte)'0';
        type[1] = (byte)'x';
        const string hex = "0123456789ABCDEF";
        type[2] = (byte)hex[item.DataType >> 4];
        type[3] = (byte)hex[item.DataType & 0x0F];

        var builder = new JsonSpanBuilder(_eventPayloadBuffer);
        builder.AppendNumber("srcId"u8, item.SourceId);
        builder.AppendNumber("hotspotId"u8, item.HotspotId);
        builder.AppendString("dataTypeHex"u8, type);
        builder.AppendHexString("hexDump"u8, slot.Buffer.AsSpan(0, slot.Length));
        builder.AppendTimestamp("timestamp"u8, item.OccurredAtTicks);
        builder.Finish();
        _mqttClient.Publish(BuildTopic(_eventTopicBuffer, "diag/unknown_frame"u8),
            _eventPayloadBuffer.AsSpan(0, builder.Length), retain: false);
    }

    private void PublishLocation(in DmrLocationEvent location)
    {
        var latitude = location.Latitude;
        var longitude = location.Longitude;
        if (_locationPrivacyEnabled && latitude.HasValue && longitude.HasValue)
        {
            LocationPrivacy.SnapToGrid(latitude.Value, longitude.Value, _locationGridKilometers,
                out var snappedLatitude, out var snappedLongitude);
            latitude = snappedLatitude;
            longitude = snappedLongitude;
        }

        var builder = new JsonSpanBuilder(_eventPayloadBuffer);
        builder.AppendNumber("srcId"u8, location.SourceId);
        builder.AppendNumber("dstId"u8, location.DestinationId);
        builder.AppendNumber("hotspotId"u8, location.HotspotId);
        AppendNullableDecimal(ref builder, "lat"u8, latitude, 6);
        AppendNullableDecimal(ref builder, "lon"u8, longitude, 6);
        AppendNullableDecimal(ref builder, "speedMps"u8, location.SpeedMetersPerSecond, 3);
        AppendNullableDecimal(ref builder, "courseDeg"u8, location.CourseDegrees, 2);
        AppendNullableDecimal(ref builder, "altitudeM"u8, location.AltitudeMeters, 1);
        builder.AppendBool("fixValid"u8, location.FixValid);
        builder.AppendString("format"u8, location.Format switch
        {
            DmrLocationFormat.NmeaRmc => "NMEA_RMC"u8,
            DmrLocationFormat.AnytoneGpsText => "ANYTONE_GPS_TEXT"u8,
            _ => "UNKNOWN"u8
        });
        builder.AppendBool("obfuscated"u8, _locationPrivacyEnabled);
        if (_locationPrivacyEnabled) builder.AppendDecimal("precisionKm"u8, _locationGridKilometers, 1);
        else builder.AppendNull("precisionKm"u8);
        builder.AppendTimestamp("timestamp"u8, location.OccurredAtTicks);
        builder.Finish();
        _mqttClient.Publish(BuildTopic(_eventTopicBuffer, "sds/gps"u8),
            _eventPayloadBuffer.AsSpan(0, builder.Length), retain: false);
    }

    private static void AppendNullableDecimal(
        ref JsonSpanBuilder builder, ReadOnlySpan<byte> key, double? value, byte precision)
    {
        if (value.HasValue) builder.AppendDecimal(key, value.Value, precision);
        else builder.AppendNull(key);
    }

    private static ReadOnlySpan<byte> BuildCallPayload(
        in DmrCallEvent call, bool includeEnd, Span<byte> target)
    {
        var builder = new JsonSpanBuilder(target);
        builder.AppendNumber("srcId"u8, call.SourceId);
        builder.AppendNumber("dstId"u8, call.DestinationId);
        builder.AppendString("type"u8, call.IsGroupCall ? "GroupCall"u8 : "PrivateCall"u8);
        builder.AppendNumber("hotspotId"u8, call.HotspotId);
        if (includeEnd)
        {
            builder.AppendDecimal("durationSec"u8, call.DurationTicks / (double)TimeSpan.TicksPerSecond);
            builder.AppendString("endReason"u8,
                call.EventType == DmrCallEventType.Ended ? "Terminated"u8 : "Timeout"u8);
        }
        builder.AppendTimestamp("timestamp"u8, call.OccurredAtTicks);
        builder.Finish();
        return target[..builder.Length];
    }

    private void PublishStateSnapshot()
    {
        var now = DateTime.UtcNow.Ticks;
        PublishSystemInfo(now);
        PublishHotspots(now);
        PublishMeshPeers(now);
        PublishGuests(now);
        PublishAwayDevices(now);
        PublishActiveCalls();
    }

    private void PublishSystemInfo(long now)
    {
        var builder = new JsonSpanBuilder(_statePayloadBuffer);
        builder.AppendNumber("zoneId"u8, _zoneId);
        builder.AppendNumber("uptimeSec"u8, Math.Max(0, now - _startedAtTicks) / TimeSpan.TicksPerSecond);
        builder.AppendString("version"u8, _version);
        builder.AppendNumber("activeCalls"u8, _router.ActiveCallCount);
        builder.AppendDecimal("memoryUsageMb"u8, GC.GetTotalMemory(false) / 1048576d);
        builder.AppendNumber("droppedMqttEvents"u8, DroppedEvents);
        builder.AppendTimestamp("timestamp"u8, now);
        builder.Finish();
        _mqttClient.Publish(BuildTopic(_stateTopicBuffer, "sys/info"u8),
            _statePayloadBuffer.AsSpan(0, builder.Length), retain: true);
    }

    private void PublishHotspots(long now)
    {
        _currentHotspots.Clear();
        Span<byte> endpoint = stackalloc byte[32];
        foreach (var pair in _repeaterRegistry.GetAll())
        {
            var repeater = pair.Value;
            _currentHotspots.Add(repeater.Id);
            var endpointLength = 0;
            if (repeater.EndPoint is { } value && !value.TryFormatUtf8(endpoint, out endpointLength))
                throw new ArgumentException("Endpoint buffer is too small.");

            var lastPing = Volatile.Read(ref repeater.LastPingTicks);
            var login = Volatile.Read(ref repeater.LoggedInSinceTicks);
            var builder = new JsonSpanBuilder(_statePayloadBuffer);
            builder.AppendNumber("repeaterId"u8, repeater.Id);
            builder.AppendNumber("householdId"u8, repeater.Id / 100);
            builder.AppendString("endpoint"u8, endpoint[..endpointLength]);
            builder.AppendNumber("uptimeSec"u8, login > 0 ? Math.Max(0, now - login) / TimeSpan.TicksPerSecond : 0);
            builder.AppendString("state"u8, GetRepeaterState(repeater.State));
            builder.AppendNumber("lastPingSecAgo"u8, lastPing > 0 ? Math.Max(0, now - lastPing) / TimeSpan.TicksPerSecond : 0);
            builder.Finish();
            _mqttClient.Publish(BuildEntityTopic(_stateTopicBuffer, "routing/hotspots/"u8, repeater.Id),
                _statePayloadBuffer.AsSpan(0, builder.Length), retain: true);
        }
        PublishTombstones("routing/hotspots/"u8, _publishedHotspots, _currentHotspots);
        Swap(ref _publishedHotspots, ref _currentHotspots);
    }

    private void PublishMeshPeers(long now)
    {
        _currentPeers.Clear();
        Span<byte> endpoint = stackalloc byte[32];
        foreach (var pair in _masterRegistry.GetAll())
        {
            var peer = pair.Value;
            _currentPeers.Add(peer.ZoneId);
            if (!peer.DataEndPoint.TryFormatUtf8(endpoint, out var endpointLength))
                throw new ArgumentException("Endpoint buffer is too small.");
            var lastSeen = Volatile.Read(ref peer.LastSeenTicks);
            var builder = new JsonSpanBuilder(_statePayloadBuffer);
            builder.AppendNumber("zoneId"u8, peer.ZoneId);
            builder.AppendString("endpoint"u8, endpoint[..endpointLength]);
            builder.AppendNumber("lastSeenSecAgo"u8, Math.Max(0, now - lastSeen) / TimeSpan.TicksPerSecond);
            builder.AppendString("status"u8, "Online"u8);
            builder.Finish();
            _mqttClient.Publish(BuildEntityTopic(_stateTopicBuffer, "routing/mesh/peers/"u8, peer.ZoneId),
                _statePayloadBuffer.AsSpan(0, builder.Length), retain: true);
        }
        PublishTombstones("routing/mesh/peers/"u8, _publishedPeers, _currentPeers);
        Swap(ref _publishedPeers, ref _currentPeers);
    }

    private void PublishGuests(long now)
    {
        _currentGuests.Clear();
        var count = _roamingRegistry.CopyLocalGuests(_guestSnapshots);
        for (var i = 0; i < count; i++)
        {
            var guest = _guestSnapshots[i];
            _currentGuests.Add(guest.DeviceId);
            var builder = new JsonSpanBuilder(_statePayloadBuffer);
            builder.AppendNumber("deviceId"u8, guest.DeviceId);
            builder.AppendNumber("homeZone"u8, guest.DeviceId / 100);
            builder.AppendNumber("connectedViaHotspot"u8, guest.HotspotId);
            builder.AppendNumber("activeSinceMin"u8, Math.Max(0, now - guest.ActiveSinceTicks) / TimeSpan.TicksPerMinute);
            builder.AppendNumber("lastSeenSecAgo"u8, Math.Max(0, now - guest.LastSeenTicks) / TimeSpan.TicksPerSecond);
            builder.Finish();
            _mqttClient.Publish(BuildEntityTopic(_stateTopicBuffer, "routing/roaming/guests/"u8, guest.DeviceId),
                _statePayloadBuffer.AsSpan(0, builder.Length), retain: true);
        }
        PublishTombstones("routing/roaming/guests/"u8, _publishedGuests, _currentGuests);
        Swap(ref _publishedGuests, ref _currentGuests);
    }

    private void PublishAwayDevices(long now)
    {
        _currentAway.Clear();
        var count = _roamingRegistry.CopyAwayDevices(_awaySnapshots);
        for (var i = 0; i < count; i++)
        {
            var away = _awaySnapshots[i];
            _currentAway.Add(away.DeviceId);
            var builder = new JsonSpanBuilder(_statePayloadBuffer);
            builder.AppendNumber("deviceId"u8, away.DeviceId);
            builder.AppendNumber("currentForeignZone"u8, away.CurrentZoneId);
            builder.AppendNumber("lastUpdateSecAgo"u8, Math.Max(0, now - away.LastSeenTicks) / TimeSpan.TicksPerSecond);
            builder.Finish();
            _mqttClient.Publish(BuildEntityTopic(_stateTopicBuffer, "routing/roaming/away/"u8, away.DeviceId),
                _statePayloadBuffer.AsSpan(0, builder.Length), retain: true);
        }
        PublishTombstones("routing/roaming/away/"u8, _publishedAway, _currentAway);
        Swap(ref _publishedAway, ref _currentAway);
    }

    private void PublishActiveCalls()
    {
        _currentCalls.Clear();
        var count = _router.CopyActiveCalls(_callSnapshots);
        for (var i = 0; i < count; i++)
        {
            var call = _callSnapshots[i];
            _currentCalls.Add(call.SourceId);
            var eventValue = new DmrCallEvent(call.SourceId, call.DestinationId, call.HotspotId,
                call.IsGroupCall, DmrCallEventType.Started, call.StartedAtTicks);
            var payload = BuildCallPayload(eventValue, includeEnd: false, _statePayloadBuffer);
            _mqttClient.Publish(BuildEntityTopic(_stateTopicBuffer, "call/active/"u8, call.SourceId),
                payload, retain: true);
        }
        PublishTombstones("call/active/"u8, _publishedCalls, _currentCalls);
        Swap(ref _publishedCalls, ref _currentCalls);
    }

    private void PublishTombstones(ReadOnlySpan<byte> subtree, HashSet<int> previous, HashSet<int> current)
    {
        foreach (var id in previous)
        {
            if (!current.Contains(id))
                _mqttClient.Publish(BuildEntityTopic(_stateTopicBuffer, subtree, id), ReadOnlySpan<byte>.Empty, retain: true);
        }
    }

    private ReadOnlySpan<byte> BuildTopic(Span<byte> target, ReadOnlySpan<byte> subtree)
    {
        var offset = BuildTopicPrefix(target);
        subtree.CopyTo(target[offset..]);
        return target[..(offset + subtree.Length)];
    }

    private ReadOnlySpan<byte> BuildEntityTopic(Span<byte> target, ReadOnlySpan<byte> subtree, int id)
    {
        var offset = BuildTopicPrefix(target);
        subtree.CopyTo(target[offset..]);
        offset += subtree.Length;
        if (!Utf8Formatter.TryFormat(id, target[offset..], out var written))
            throw new ArgumentException("MQTT topic buffer is too small.");
        return target[..(offset + written)];
    }

    private int BuildTopicPrefix(Span<byte> target)
    {
        "dmroute/"u8.CopyTo(target);
        var offset = 8;
        if (!Utf8Formatter.TryFormat(_zoneId, target[offset..], out var written))
            throw new ArgumentException("MQTT topic buffer is too small.");
        offset += written;
        target[offset++] = (byte)'/';
        return offset;
    }

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
        _sdsGateway.OnSmsReceived -= HandleSms;
        _sdsGateway.OnLocationReceived -= HandleLocation;
        base.Dispose();
    }
}
