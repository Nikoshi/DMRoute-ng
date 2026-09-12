using System.Buffers.Binary;
using DMRoute_ng.Core;
using DMRoute_ng.Registry;
using DMRoute_ng.Types;
using Microsoft.Extensions.Logging;

namespace DMRoute_ng.Routing;

public delegate void DmrDataFrameHandler(ReadOnlySpan<byte> packet);
public delegate void DmrCallEventHandler(in DmrCallEvent callEvent);
public delegate void DmrUnknownFrameHandler(ReadOnlySpan<byte> packet, int sourceId, int hotspotId, byte dataType, long occurredAtTicks);

public enum DmrCallEventType : byte
{
    Started,
    Ended,
    TimedOut
}

public readonly struct DmrCallEvent(
    int sourceId,
    int destinationId,
    int hotspotId,
    bool isGroupCall,
    DmrCallEventType eventType,
    long occurredAtTicks,
    long durationTicks = 0)
{
    public int SourceId { get; } = sourceId;
    public int DestinationId { get; } = destinationId;
    public int HotspotId { get; } = hotspotId;
    public bool IsGroupCall { get; } = isGroupCall;
    public DmrCallEventType EventType { get; } = eventType;
    public long OccurredAtTicks { get; } = occurredAtTicks;
    public long DurationTicks { get; } = durationTicks;
}

public readonly struct ActiveCallSnapshot(
    int sourceId,
    int destinationId,
    int hotspotId,
    bool isGroupCall,
    long startedAtTicks)
{
    public int SourceId { get; } = sourceId;
    public int DestinationId { get; } = destinationId;
    public int HotspotId { get; } = hotspotId;
    public bool IsGroupCall { get; } = isGroupCall;
    public long StartedAtTicks { get; } = startedAtTicks;
}

public sealed partial class MicroSubnetRouter : IDisposable
{
    private readonly struct CallState(
        int dstId,
        int hotspotId,
        bool isGroupCall,
        long startTicks,
        long lastFrameTicks,
        long terminationTicks = 0,
        bool pendingTermination = false)
    {
        public readonly int DstId = dstId;
        public readonly int HotspotId = hotspotId;
        public readonly bool IsGroupCall = isGroupCall;
        public readonly long StartTicks = startTicks;
        public readonly long LastFrameTicks = lastFrameTicks;
        public readonly long TerminationTicks = terminationTicks;
        public readonly bool PendingTermination = pendingTermination;
    }

    private readonly struct ExpiredCall(int sourceId, CallState state, byte eventType)
    {
        public readonly int SourceId = sourceId;
        public readonly CallState State = state;
        public readonly byte EventType = eventType;
    }

    private readonly ILogger<MicroSubnetRouter> _logger;
    private readonly RepeaterRegistry _registry;
    private readonly MasterRegistry _masterRegistry;
    private readonly RoamingRegistry _roamingRegistry;
    private readonly MeshDiscoveryService _meshService;
    private readonly int _masterZoneId;
    private readonly int _maxLocalDeviceRoutes;
    private readonly int _maxActiveCalls;

    private readonly Dictionary<int, int> _localDeviceRouting;
    private readonly Dictionary<int, CallState> _activeCalls;
    private readonly object _activeCallsLock = new();
    private readonly ExpiredCall[] _expiredCalls;
    private readonly Timer _cleanupTimer;
    private long _droppedLocalRouteStates;
    private long _droppedCallStates;

    public event DmrDataFrameHandler? OnDataFrameReceived;
    public event DmrCallEventHandler? OnCallEvent;
    public event DmrUnknownFrameHandler? OnUnknownFrameReceived;

    public long DroppedLocalRouteStates => Interlocked.Read(ref _droppedLocalRouteStates);
    public long DroppedCallStates => Interlocked.Read(ref _droppedCallStates);
    public int MaxActiveCalls => _maxActiveCalls;

    public int ActiveCallCount
    {
        get
        {
            lock (_activeCallsLock) return _activeCalls.Count;
        }
    }

    public MicroSubnetRouter(
        ILogger<MicroSubnetRouter> logger,
        RepeaterRegistry registry,
        MasterRegistry masterRegistry,
        RoamingRegistry roamingRegistry,
        MeshDiscoveryService meshService,
        int masterZoneId,
        int maxActiveCalls = 256,
        int maxLocalDeviceRoutes = 8192)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxActiveCalls);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLocalDeviceRoutes);

        _logger = logger;
        _registry = registry;
        _masterRegistry = masterRegistry;
        _roamingRegistry = roamingRegistry;
        _meshService = meshService;
        _masterZoneId = masterZoneId;
        _maxActiveCalls = maxActiveCalls;
        _maxLocalDeviceRoutes = maxLocalDeviceRoutes;
        _localDeviceRouting = new Dictionary<int, int>(maxLocalDeviceRoutes);
        _activeCalls = new Dictionary<int, CallState>(maxActiveCalls);
        _expiredCalls = new ExpiredCall[maxActiveCalls];
        _cleanupTimer = new Timer(CleanupStaleCalls, null, 2000, 2000);
    }

    public void RouteDmrd(ReadOnlySpan<byte> packet, Ipv4Endpoint remoteEndPoint, IDmrSender sender)
    {
        if (packet.Length < 23) return;

        var srcId = (packet[5] << 16) | (packet[6] << 8) | packet[7];
        var dstId = (packet[8] << 16) | (packet[9] << 8) | packet[10];
        var repeaterId = BinaryPrimitives.ReadInt32BigEndian(packet.Slice(11, 4));
        var bits = packet[15];
        var isUnitCall = (bits & 0x40) != 0;
        var isGroupCall = !isUnitCall;
        var isDataFrame = (bits & 0x20) != 0;
        var dataType = (byte)(bits & 0x0F);

        var isLocalOrigin = _registry.TryGetExisting(repeaterId, out var sourceRepeater)
                            && sourceRepeater.State == RepeaterState.LoggedIn
                            && sourceRepeater.EndPoint == remoteEndPoint;
        if (isLocalOrigin) Volatile.Write(ref sourceRepeater!.LastPingTicks, DateTime.UtcNow.Ticks);

        var isMeshOrigin = false;
        if (!isLocalOrigin)
        {
            var originZoneId = repeaterId / 10000;
            isMeshOrigin = _masterRegistry.TryGet(originZoneId, out var masterPeer)
                           && masterPeer.DataEndPoint == remoteEndPoint;
        }
        if (!isLocalOrigin && !isMeshOrigin) return;

        if (isLocalOrigin)
        {
            var sourceHomeZone = srcId / 100;
            if (sourceHomeZone == _masterZoneId)
            {
                LearnLocalRoute(srcId, repeaterId);
            }
            else
            {
                _roamingRegistry.TrackLocalGuest(srcId, repeaterId, sourceRepeater!.EndPoint!.Value);
                if (dataType is 0x01 or 0x03 && _masterRegistry.TryGet(sourceHomeZone, out var homeMaster))
                {
                    _meshService.QueueLocationUpdate(srcId, homeMaster.DataEndPoint);
                }
            }
        }

        HandleSignaling(packet, srcId, dstId, repeaterId, isGroupCall, dataType);
        if (isDataFrame) OnDataFrameReceived?.Invoke(packet);

        if (isGroupCall)
        {
            foreach (var peer in _registry.GetRoutingSnapshot())
            {
                if (peer.State != RepeaterState.LoggedIn || peer.EndPoint is not { } target ||
                    (isLocalOrigin && peer.Id == repeaterId)) continue;
                sender.SendTo(packet, target);
            }

            if (!isLocalOrigin) return;
            if (dstId == 1)
            {
                foreach (var master in _masterRegistry.GetRoutingSnapshot()) sender.SendTo(packet, master.DataEndPoint);
            }
            else if (dstId is >= 100 and <= 999 && _masterRegistry.TryGet(dstId, out var targetMaster))
            {
                sender.SendTo(packet, targetMaster.DataEndPoint);
            }
            return;
        }

        var targetHomeZone = dstId / 100;
        if (targetHomeZone == _masterZoneId)
        {
            if (_localDeviceRouting.TryGetValue(dstId, out var targetRepeaterId) &&
                _registry.TryGetExisting(targetRepeaterId, out var targetRepeater) && targetRepeater.EndPoint is { } localTarget)
            {
                sender.SendTo(packet, localTarget);
            }
            else if (_roamingRegistry.TryGetRoamedDeviceZone(dstId, out var foreignZoneId) &&
                     _masterRegistry.TryGet(foreignZoneId, out var foreignMaster))
            {
                sender.SendTo(packet, foreignMaster.DataEndPoint);
            }
            else if (dataType is 0x01 or 0x03)
            {
                LogUnknownLocalTarget(_logger, dstId);
            }
        }
        else if (_roamingRegistry.TryGetLocalGuestEndpoint(dstId, out var guestEndpoint))
        {
            sender.SendTo(packet, guestEndpoint);
        }
        else if (_masterRegistry.TryGet(targetHomeZone, out var targetMaster))
        {
            sender.SendTo(packet, targetMaster.DataEndPoint);
            if (dataType == 0x01) LogMeshPrivateCall(_logger, srcId, dstId, targetHomeZone);
        }
        else if (dataType is 0x01 or 0x03)
        {
            LogUnknownRemoteTarget(_logger, dstId, targetHomeZone);
        }
    }

    private void LearnLocalRoute(int sourceId, int repeaterId)
    {
        if (_localDeviceRouting.TryGetValue(sourceId, out _))
        {
            _localDeviceRouting[sourceId] = repeaterId;
            return;
        }

        if (_localDeviceRouting.Count >= _maxLocalDeviceRoutes)
        {
            Interlocked.Increment(ref _droppedLocalRouteStates);
            return;
        }
        _localDeviceRouting.Add(sourceId, repeaterId);
    }

    public int CopyActiveCalls(Span<ActiveCallSnapshot> destination)
    {
        var count = 0;
        lock (_activeCallsLock)
        {
            foreach (var pair in _activeCalls)
            {
                if (count == destination.Length) break;
                var state = pair.Value;
                destination[count++] = new ActiveCallSnapshot(
                    pair.Key,
                    state.DstId,
                    state.HotspotId,
                    state.IsGroupCall,
                    state.StartTicks);
            }
        }
        return count;
    }

    private void HandleSignaling(
        ReadOnlySpan<byte> packet,
        int srcId,
        int dstId,
        int hotspotId,
        bool isGroupCall,
        byte dataType)
    {
        var now = DateTime.UtcNow.Ticks;
        var publishStart = false;

        switch (dataType)
        {
            case 0x01:
                lock (_activeCallsLock)
                {
                    if (_activeCalls.TryGetValue(srcId, out var existing))
                    {
                        _activeCalls[srcId] = new CallState(
                            dstId,
                            hotspotId,
                            isGroupCall,
                            existing.StartTicks,
                            now);
                    }
                    else if (_activeCalls.Count < _maxActiveCalls)
                    {
                        _activeCalls.Add(srcId, new CallState(dstId, hotspotId, isGroupCall, now, now));
                        publishStart = true;
                    }
                    else
                    {
                        Interlocked.Increment(ref _droppedCallStates);
                    }
                }

                if (publishStart)
                {
                    LogCallStart(_logger, isGroupCall ? "GroupCall" : "PrivateCall", srcId, dstId);
                    var callEvent = new DmrCallEvent(
                        srcId,
                        dstId,
                        hotspotId,
                        isGroupCall,
                        DmrCallEventType.Started,
                        now);
                    OnCallEvent?.Invoke(in callEvent);
                }
                break;
            case 0x02:
                lock (_activeCallsLock)
                {
                    if (_activeCalls.TryGetValue(srcId, out var active))
                        _activeCalls[srcId] = new CallState(
                            active.DstId,
                            active.HotspotId,
                            active.IsGroupCall,
                            active.StartTicks,
                            now,
                            now,
                            true);
                }
                break;
            case 0x03:
                LogCsbk(_logger, srcId, dstId);
                break;
            case <= 0x08:
                lock (_activeCallsLock)
                {
                    if (_activeCalls.TryGetValue(srcId, out var current))
                        _activeCalls[srcId] = new CallState(
                            current.DstId,
                            current.HotspotId,
                            current.IsGroupCall,
                            current.StartTicks,
                            now,
                            current.TerminationTicks,
                            current.PendingTermination);
                }
                break;
            default:
                OnUnknownFrameReceived?.Invoke(packet, srcId, hotspotId, dataType, now);
                break;
        }
    }

    private void CleanupStaleCalls(object? state)
    {
        var currentTicks = DateTime.UtcNow.Ticks;
        var count = 0;

        lock (_activeCallsLock)
        {
            foreach (var pair in _activeCalls)
            {
                var call = pair.Value;
                var elapsed = currentTicks - call.LastFrameTicks;
                var eventType = call.PendingTermination && elapsed > TimeSpan.FromMilliseconds(1500).Ticks
                    ? (byte)0x02
                    : !call.PendingTermination && elapsed > TimeSpan.FromSeconds(3).Ticks
                        ? (byte)0xFE
                        : (byte)0;
                if (eventType != 0) _expiredCalls[count++] = new ExpiredCall(pair.Key, call, eventType);
            }

            for (var i = 0; i < count; i++) _activeCalls.Remove(_expiredCalls[i].SourceId);
        }

        for (var i = 0; i < count; i++)
        {
            var expired = _expiredCalls[i];
            var endedAtTicks = expired.EventType == 0x02
                ? expired.State.TerminationTicks
                : expired.State.LastFrameTicks;
            var durationTicks = Math.Max(0, endedAtTicks - expired.State.StartTicks);
            if (expired.EventType == 0x02)
                LogCallEnd(_logger, expired.State.IsGroupCall ? "GroupCall" : "PrivateCall", expired.SourceId, expired.State.DstId);
            else
                LogCallTimeout(_logger, expired.SourceId, expired.State.DstId);
            var callEvent = new DmrCallEvent(
                expired.SourceId,
                expired.State.DstId,
                expired.State.HotspotId,
                expired.State.IsGroupCall,
                expired.EventType == 0x02 ? DmrCallEventType.Ended : DmrCallEventType.TimedOut,
                expired.EventType == 0x02 ? endedAtTicks : currentTicks,
                durationTicks);
            OnCallEvent?.Invoke(in callEvent);
            _expiredCalls[i] = default;
        }
    }

    public void Dispose() => _cleanupTimer.Dispose();

    [LoggerMessage(1001, LogLevel.Information, "START: {CallType} von {SourceId} an {DestinationId}")]
    private static partial void LogCallStart(ILogger logger, string callType, int sourceId, int destinationId);

    [LoggerMessage(1002, LogLevel.Information, "ENDE: {CallType} von {SourceId} an {DestinationId} (sauber beendet)")]
    private static partial void LogCallEnd(ILogger logger, string callType, int sourceId, int destinationId);

    [LoggerMessage(1003, LogLevel.Warning, "TIMEOUT: Call von {SourceId} an {DestinationId} wegen Inaktivität abgebrochen")]
    private static partial void LogCallTimeout(ILogger logger, int sourceId, int destinationId);

    [LoggerMessage(1004, LogLevel.Warning, "Lokales Ziel {DestinationId} unbekannt und kein Roaming-Eintrag")]
    private static partial void LogUnknownLocalTarget(ILogger logger, int destinationId);

    [LoggerMessage(1005, LogLevel.Information, "Mesh PrivateCall-Start von {SourceId} an {DestinationId} (Zone {Zone})")]
    private static partial void LogMeshPrivateCall(ILogger logger, int sourceId, int destinationId, int zone);

    [LoggerMessage(1006, LogLevel.Debug, "Ziel {DestinationId} (Zone {Zone}) unbekannt oder offline")]
    private static partial void LogUnknownRemoteTarget(ILogger logger, int destinationId, int zone);

    [LoggerMessage(1008, LogLevel.Debug, "CSBK: Signalisierung von {SourceId} an {DestinationId}")]
    private static partial void LogCsbk(ILogger logger, int sourceId, int destinationId);
}
