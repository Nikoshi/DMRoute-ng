using System.Collections.Concurrent;
using System.Threading.Channels;
using DMRoute_ng.Coding;
using DMRoute_ng.Configuration;
using DMRoute_ng.Core;
using DMRoute_ng.Registry;
using DMRoute_ng.Routing;
using DMRoute_ng.Types;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DMRoute_ng.Services;

/// <summary>
/// Löst fehlende Radio-Check-Ziele über lokale Beobachtungen und das Master-Mesh auf
/// und verwaltet die persistente Fallback-SDS-Zustellung.
/// </summary>
/// <remarks>
/// Ereigniskanäle, Auflösungen, Caches und Outbox sind konfiguriert begrenzt. Der
/// synchrone Routerpfad stellt nur kompakte Ereignisse ein; Netzabfragen und Funkfolgen
/// laufen außerhalb des Empfangspfads.
/// </remarks>
public sealed partial class RadioCheckReachabilityCoordinator : BackgroundService
{
    private enum EventKind : byte
    {
        RadioCheckMiss,
        RadioCheckAcknowledgement,
        MeshControl,
        SdsDelivery
    }

    private readonly record struct CoordinatorEvent(
        EventKind Kind,
        RadioCheckRouteMiss RouteMiss,
        RadioCheckAcknowledgement Acknowledgement,
        MeshControlMessage MeshMessage,
        Ipv4Endpoint Remote,
        FallbackSdsDelivery Delivery);

    private sealed class PendingProbe
    {
        private readonly TaskCompletionSource<bool> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> Completion => _completion.Task;
        public void Complete() => _completion.TrySetResult(true);
    }

    private sealed class PendingQuery
    {
        private readonly object _lock = new();
        private readonly TaskCompletionSource<MeshControlMessage> _first =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private MeshControlMessage _best;

        public Task<MeshControlMessage> FirstResponse => _first.Task;

        public void Add(in MeshControlMessage message)
        {
            lock (_lock)
            {
                if (message.Status == MeshRouteStatus.Found &&
                    (_best.Status != MeshRouteStatus.Found || message.ObservedAtTicks > _best.ObservedAtTicks ||
                     message.ObservedAtTicks == _best.ObservedAtTicks && message.RouteZone < _best.RouteZone))
                    _best = message;
            }
            _first.TrySetResult(message);
        }

        public MeshControlMessage Best
        {
            get
            {
                lock (_lock) return _best;
            }
        }
    }

    private struct ResolutionCacheEntry
    {
        public int RouteZone;
        public long ExpiresAtTicks;
        public long LastSentTicks;
    }

    private static readonly TimeSpan HomeTimeout = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan HomeProbeResponseTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan WhoisRepeatDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan WhoisWindow = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan PositiveCacheDuration = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan NegativeCacheDuration = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MinimumResendDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReplayRetention = TimeSpan.FromSeconds(31);
    private static readonly TimeSpan DeliveryRetryDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TransferRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BurstDelay = TimeSpan.FromMilliseconds(60);

    private readonly ILogger<RadioCheckReachabilityCoordinator> _logger;
    private readonly MicroSubnetRouter _router;
    private readonly RepeaterRegistry _repeaters;
    private readonly MasterRegistry _masters;
    private readonly RoamingRegistry _roaming;
    private readonly MeshDiscoveryService _mesh;
    private readonly IDmrSender _sender;
    private readonly FallbackSdsService _fallbackSds;
    private readonly RadioCheckOutbox _outbox;
    private readonly TimeProvider _timeProvider;
    private readonly RadioCheckSettings _settings;
    private readonly int _zoneId;
    private readonly int _maxPending;
    private readonly Channel<CoordinatorEvent> _events;
    private readonly ConcurrentDictionary<ulong, PendingQuery> _pendingQueries = new();
    private readonly ConcurrentDictionary<long, PendingProbe> _pendingProbes = new();
    private readonly Dictionary<long, ResolutionCacheEntry> _resolutionCache;
    private readonly Dictionary<ulong, long> _receivedCommands;
    private readonly object _stateLock = new();
    private int _activeResolutions;
    private long _correlationSequence = DateTime.UtcNow.Ticks;
    private int _packetSequence;
    private int _streamSequence;
    private long _droppedEvents;
    private long _droppedResolutions;
    private long _droppedFallbacks;

    public RadioCheckReachabilityCoordinator(
        ILogger<RadioCheckReachabilityCoordinator> logger,
        MicroSubnetRouter router,
        RepeaterRegistry repeaters,
        MasterRegistry masters,
        RoamingRegistry roaming,
        MeshDiscoveryService mesh,
        IDmrSender sender,
        FallbackSdsService fallbackSds,
        RadioCheckOutbox outbox,
        DmRouteSettings settings,
        TimeProvider? timeProvider = null)
    {
        _logger = logger;
        _router = router;
        _repeaters = repeaters;
        _masters = masters;
        _roaming = roaming;
        _mesh = mesh;
        _sender = sender;
        _fallbackSds = fallbackSds;
        _outbox = outbox;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _settings = settings.RadioCheck;
        _zoneId = settings.ZoneId;
        _maxPending = settings.RadioCheck.MaxPending;
        _resolutionCache = new Dictionary<long, ResolutionCacheEntry>(_maxPending);
        _receivedCommands = new Dictionary<ulong, long>(_maxPending * 2);
        _events = Channel.CreateBounded<CoordinatorEvent>(new BoundedChannelOptions(_maxPending * 4)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });

        router.OnRadioCheckRouteMiss += HandleRouteMiss;
        router.OnRadioCheckAcknowledgement += HandleRadioCheckAcknowledgement;
        mesh.ControlMessageReceived += HandleMeshControl;
        fallbackSds.DeliveryCompleted += HandleDeliveryCompleted;
    }

    /// <summary>Anzahl verworfener Eingangsereignisse bei voller interner Queue.</summary>
    public long DroppedEvents => Interlocked.Read(ref _droppedEvents);
    /// <summary>Anzahl nicht gestarteter Zielauflösungen wegen erreichter Kapazität.</summary>
    public long DroppedResolutions => Interlocked.Read(ref _droppedResolutions);
    /// <summary>Anzahl nicht speicherbarer Fallback-Einträge.</summary>
    public long DroppedFallbacks => Interlocked.Read(ref _droppedFallbacks);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _timeProvider);
        var timerTask = timer.WaitForNextTickAsync(stoppingToken).AsTask();
        var eventTask = _events.Reader.WaitToReadAsync(stoppingToken).AsTask();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var completed = await Task.WhenAny(timerTask, eventTask).ConfigureAwait(false);
                if (completed == eventTask)
                {
                    if (!await eventTask.ConfigureAwait(false)) break;
                    while (_events.Reader.TryRead(out var item)) ProcessEvent(in item, stoppingToken);
                    eventTask = _events.Reader.WaitToReadAsync(stoppingToken).AsTask();
                }
                if (completed == timerTask)
                {
                    if (!await timerTask.ConfigureAwait(false)) break;
                    ProcessOutbox();
                    CleanupState();
                    timerTask = timer.WaitForNextTickAsync(stoppingToken).AsTask();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private void ProcessEvent(in CoordinatorEvent item, CancellationToken token)
    {
        switch (item.Kind)
        {
            case EventKind.RadioCheckMiss:
                var routeMiss = item.RouteMiss;
                StartResolution(in routeMiss, token);
                break;
            case EventKind.RadioCheckAcknowledgement:
                var acknowledgement = item.Acknowledgement;
                CompleteProbe(in acknowledgement);
                break;
            case EventKind.MeshControl:
                var meshMessage = item.MeshMessage;
                ProcessMeshControl(in meshMessage, item.Remote);
                break;
            case EventKind.SdsDelivery:
                var delivery = item.Delivery;
                ProcessDelivery(in delivery);
                break;
        }
    }

    private void HandleRouteMiss(in RadioCheckRouteMiss miss)
    {
        var item = new CoordinatorEvent(EventKind.RadioCheckMiss, miss, default, default, default, default);
        if (!_events.Writer.TryWrite(item)) Interlocked.Increment(ref _droppedEvents);
    }

    private void HandleRadioCheckAcknowledgement(in RadioCheckAcknowledgement acknowledgement)
    {
        var item = new CoordinatorEvent(
            EventKind.RadioCheckAcknowledgement, default, acknowledgement, default, default, default);
        if (!_events.Writer.TryWrite(item)) Interlocked.Increment(ref _droppedEvents);
    }

    private void CompleteProbe(in RadioCheckAcknowledgement acknowledgement)
    {
        var key = PairKey(acknowledgement.DestinationId, acknowledgement.SourceId);
        if (_pendingProbes.TryGetValue(key, out var pending)) pending.Complete();
    }

    private void HandleMeshControl(in MeshControlMessage message, Ipv4Endpoint remote)
    {
        var item = new CoordinatorEvent(EventKind.MeshControl, default, default, message, remote, default);
        if (!_events.Writer.TryWrite(item)) Interlocked.Increment(ref _droppedEvents);
    }

    private void HandleDeliveryCompleted(in FallbackSdsDelivery delivery)
    {
        var item = new CoordinatorEvent(EventKind.SdsDelivery, default, default, default, default, delivery);
        if (!_events.Writer.TryWrite(item)) Interlocked.Increment(ref _droppedEvents);
    }

    private void CleanupState()
    {
        var now = _timeProvider.GetUtcNow().Ticks;
        lock (_stateLock)
        {
            foreach (var key in _resolutionCache.Where(pair => pair.Value.ExpiresAtTicks <= now).Select(pair => pair.Key).ToArray())
                _resolutionCache.Remove(key);
            foreach (var key in _receivedCommands.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray())
                _receivedCommands.Remove(key);
        }
    }

    private void RemoveOldestCommand()
    {
        var oldestKey = 0UL;
        var oldestTicks = long.MaxValue;
        foreach (var pair in _receivedCommands)
        {
            if (pair.Value >= oldestTicks) continue;
            oldestKey = pair.Key;
            oldestTicks = pair.Value;
        }
        _receivedCommands.Remove(oldestKey);
    }

    private ulong NextCorrelationId() =>
        ((ulong)(uint)_zoneId << 32) | unchecked((uint)Interlocked.Increment(ref _correlationSequence));

    private static long PairKey(int sourceId, int targetId) => ((long)sourceId << 32) | (uint)targetId;

    public override void Dispose()
    {
        _router.OnRadioCheckRouteMiss -= HandleRouteMiss;
        _router.OnRadioCheckAcknowledgement -= HandleRadioCheckAcknowledgement;
        _mesh.ControlMessageReceived -= HandleMeshControl;
        _fallbackSds.DeliveryCompleted -= HandleDeliveryCompleted;
        base.Dispose();
    }

    [LoggerMessage(3301, LogLevel.Information,
        "Radio Check von {SourceId} an {TargetId} über Zone {RouteZone} aufgelöst")]
    private static partial void LogResolved(ILogger logger, int sourceId, int targetId, int routeZone);

    [LoggerMessage(3302, LogLevel.Information,
        "Radio Check von {SourceId} an {TargetId} ohne Pfad; Fallback-SDS wird bis zu einem neuen Zielpfad gepuffert")]
    private static partial void LogUnresolved(ILogger logger, int sourceId, int targetId);

    [LoggerMessage(3303, LogLevel.Information,
        "Heimat-Probe für Radio Check von {SourceId} an {TargetId} über {HotspotCount} Hotspots gestartet")]
    private static partial void LogHomeProbeStarted(ILogger logger, int sourceId, int targetId, int hotspotCount);

    [LoggerMessage(3304, LogLevel.Information,
        "Heimat-Probe für Radio Check von {SourceId} an {TargetId} wurde vom Ziel bestätigt")]
    private static partial void LogHomeProbeAcknowledged(ILogger logger, int sourceId, int targetId);

    [LoggerMessage(3305, LogLevel.Information,
        "Heimat-Probe für Radio Check von {SourceId} an {TargetId} blieb ohne Antwort")]
    private static partial void LogHomeProbeTimedOut(ILogger logger, int sourceId, int targetId);
}
