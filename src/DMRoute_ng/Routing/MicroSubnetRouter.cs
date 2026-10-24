using System.Buffers.Binary;
using DMRoute_ng.Coding;
using DMRoute_ng.Core;
using DMRoute_ng.Registry;
using DMRoute_ng.Services;
using DMRoute_ng.Types;
using Microsoft.Extensions.Logging;

namespace DMRoute_ng.Routing;

/// <summary>
/// Verarbeitet ein DMR-Datenframe synchron. Der Paketspan ist nur während des
/// Rückrufs gültig und muss bei dauerhafter Nutzung kopiert werden.
/// </summary>
public delegate void DmrDataFrameHandler(ReadOnlySpan<byte> packet);
public delegate void DmrCallEventHandler(in DmrCallEvent callEvent);
public delegate void DmrUnknownFrameHandler(ReadOnlySpan<byte> packet, int sourceId, int hotspotId, byte dataType, long occurredAtTicks);
public delegate void RadioCheckRouteMissHandler(in RadioCheckRouteMiss routeMiss);
public delegate void RadioCheckAcknowledgementHandler(in RadioCheckAcknowledgement acknowledgement);

public readonly record struct RadioCheckRouteMiss(
    int SourceId,
    int TargetId,
    int SourceHotspotId,
    long OccurredAtTicks);

public readonly record struct RadioCheckAcknowledgement(
    int SourceId,
    int DestinationId,
    long OccurredAtTicks);

public readonly record struct RadioTargetRoute(
    int HotspotId,
    Ipv4Endpoint EndPoint,
    byte ColorCode,
    long LastSeenTicks);

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

/// <summary>
/// Prüft die Herkunft eingehender DMRD-Pakete, lernt lokale Teilnehmerpfade und
/// leitet Gruppen-, Privat- und Dienstdaten an lokale oder entfernte Ziele weiter.
/// </summary>
/// <remarks>
/// Die synchrone Paketverarbeitung ist ein Hot Path. Zustände sind vorab begrenzt;
/// bei voller Kapazität wird weitergeroutet und nur zusätzlicher Beobachtungszustand
/// verworfen.
/// </remarks>
public sealed partial class MicroSubnetRouter : IDisposable
{
    private readonly struct LocalDeviceRoute(int hotspotId, long lastSeenTicks)
    {
        public readonly int HotspotId = hotspotId;
        public readonly long LastSeenTicks = lastSeenTicks;
    }

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

    private readonly Dictionary<int, LocalDeviceRoute> _localDeviceRouting;
    private readonly object _localRoutesLock = new();
    private readonly Dictionary<int, CallState> _activeCalls;
    private readonly object _activeCallsLock = new();
    private readonly ExpiredCall[] _expiredCalls;
    private readonly Timer _cleanupTimer;
    private long _droppedLocalRouteStates;
    private long _droppedCallStates;
    private long _droppedReservedSources;
    private long _consumedServiceFrames;

    public event DmrDataFrameHandler? OnDataFrameReceived;
    public event DmrCallEventHandler? OnCallEvent;
    public event DmrUnknownFrameHandler? OnUnknownFrameReceived;
    public event RadioCheckRouteMissHandler? OnRadioCheckRouteMiss;
    public event RadioCheckAcknowledgementHandler? OnRadioCheckAcknowledgement;

    public long DroppedLocalRouteStates => Interlocked.Read(ref _droppedLocalRouteStates);
    public long DroppedCallStates => Interlocked.Read(ref _droppedCallStates);
    public long DroppedReservedSources => Interlocked.Read(ref _droppedReservedSources);
    public long ConsumedServiceFrames => Interlocked.Read(ref _consumedServiceFrames);
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
        _localDeviceRouting = new Dictionary<int, LocalDeviceRoute>(maxLocalDeviceRoutes);
        _activeCalls = new Dictionary<int, CallState>(maxActiveCalls);
        _expiredCalls = new ExpiredCall[maxActiveCalls];
        _cleanupTimer = new Timer(CleanupStaleCalls, null, 2000, 2000);
    }

    /// <summary>
    /// Verarbeitet und routet ein DMRD-Datagramm synchron. Weder Router noch Sender
    /// dürfen den geliehenen Paketspan über den Aufruf hinaus speichern.
    /// </summary>
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

        if (DmrServiceIds.IsReserved(srcId))
        {
            Interlocked.Increment(ref _droppedReservedSources);
            return;
        }

        if (DmrServiceIds.IsReserved(dstId))
        {
            Interlocked.Increment(ref _consumedServiceFrames);
            if (isDataFrame) OnDataFrameReceived?.Invoke(packet);
            return;
        }

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

        DmrControlMessage controlMessage = default;
        var hasControlMessage = dataType == 0x03 &&
                                DmrBurstWriter.TryReadControlMessage(packet, out controlMessage);
        if (hasControlMessage && controlMessage.Type == DmrControlMessageType.RadioCheckAcknowledgement)
        {
            var acknowledgement = new RadioCheckAcknowledgement(srcId, dstId, DateTime.UtcNow.Ticks);
            OnRadioCheckAcknowledgement?.Invoke(in acknowledgement);
        }

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

        var routed = false;
        var targetHomeZone = dstId / 100;
        if (targetHomeZone == _masterZoneId)
        {
            if (TryGetLocalHomeTarget(dstId, out var localRoute))
            {
                sender.SendTo(packet, localRoute.EndPoint);
                routed = true;
            }
            else if (_roamingRegistry.TryGetRoamedDeviceZone(dstId, out var foreignZoneId) &&
                     _masterRegistry.TryGet(foreignZoneId, out var foreignMaster))
            {
                sender.SendTo(packet, foreignMaster.DataEndPoint);
                routed = true;
            }
            else if (dataType == 0x01)
            {
                LogUnknownLocalTarget(_logger, dstId);
            }
        }
        else if (TryGetLocalGuestTarget(dstId, out var guestRoute))
        {
            sender.SendTo(packet, guestRoute.EndPoint);
            routed = true;
        }
        else if (_masterRegistry.TryGet(targetHomeZone, out var targetMaster))
        {
            sender.SendTo(packet, targetMaster.DataEndPoint);
            routed = true;
            if (dataType == 0x01) LogMeshPrivateCall(_logger, srcId, dstId, targetHomeZone);
        }
        else if (dataType == 0x01)
        {
            LogUnknownRemoteTarget(_logger, dstId, targetHomeZone);
        }

        if (!routed && hasControlMessage && controlMessage.Type == DmrControlMessageType.RadioCheckRequest)
        {
            var routeMiss = new RadioCheckRouteMiss(srcId, dstId, repeaterId, DateTime.UtcNow.Ticks);
            OnRadioCheckRouteMiss?.Invoke(in routeMiss);
        }
    }

    /// <summary>Ermittelt einen aktuell erreichbaren lokalen Heim- oder Gastteilnehmer.</summary>
    public bool TryGetAnyLocalTarget(int deviceId, out RadioTargetRoute route) =>
        TryGetLocalGuestTarget(deviceId, out route) || TryGetLocalHomeTarget(deviceId, out route);

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
