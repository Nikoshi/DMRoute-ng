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

    public long DroppedEvents => Interlocked.Read(ref _droppedEvents);
    public long DroppedResolutions => Interlocked.Read(ref _droppedResolutions);
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

    private void StartResolution(in RadioCheckRouteMiss miss, CancellationToken token)
    {
        if (!_settings.Enabled) return;
        var now = _timeProvider.GetUtcNow().Ticks;
        var key = PairKey(miss.SourceId, miss.TargetId);
        lock (_stateLock)
        {
            if (_resolutionCache.TryGetValue(key, out var cached) && cached.ExpiresAtTicks > now)
            {
                if (cached.RouteZone > 0 && now - cached.LastSentTicks >= MinimumResendDelay.Ticks)
                {
                    cached.LastSentTicks = now;
                    _resolutionCache[key] = cached;
                    QueueRadioCheck(cached.RouteZone, NextCorrelationId(), miss.SourceId, miss.TargetId);
                }
                return;
            }

            if (_activeResolutions >= _maxPending || _resolutionCache.Count >= _maxPending)
            {
                Interlocked.Increment(ref _droppedResolutions);
                return;
            }
            _activeResolutions++;
            _resolutionCache[key] = new ResolutionCacheEntry
            {
                RouteZone = -1,
                ExpiresAtTicks = now + NegativeCacheDuration.Ticks
            };
        }

        _ = ResolveAsync(miss.SourceId, miss.TargetId, miss.SourceHotspotId, key, token);
    }

    private async Task ResolveAsync(
        int sourceId,
        int targetId,
        int sourceHotspotId,
        long cacheKey,
        CancellationToken token)
    {
        var routeZone = 0;
        var answeredByHomeProbe = false;
        try
        {
            if (_router.TryGetAnyLocalTarget(targetId, out _))
            {
                routeZone = _zoneId;
            }
            else
            {
                if (targetId / 100 == _zoneId)
                    answeredByHomeProbe = await ProbeHomeAsync(
                        sourceId, targetId, sourceHotspotId, token).ConfigureAwait(false);

                if (!answeredByHomeProbe)
                {
                    routeZone = await ResolveKnownRouteAsync(sourceId, targetId, token).ConfigureAwait(false);
                    if (routeZone == 0)
                        routeZone = await ResolveWhoisAsync(sourceId, targetId, token).ConfigureAwait(false);
                }
            }

            var now = _timeProvider.GetUtcNow().Ticks;
            lock (_stateLock)
            {
                _resolutionCache[cacheKey] = new ResolutionCacheEntry
                {
                    RouteZone = answeredByHomeProbe ? _zoneId : routeZone,
                    LastSentTicks = answeredByHomeProbe || routeZone > 0 ? now : 0,
                    ExpiresAtTicks = now +
                                     (answeredByHomeProbe || routeZone > 0
                                         ? PositiveCacheDuration
                                         : NegativeCacheDuration).Ticks
                };
            }

            if (answeredByHomeProbe)
            {
                LogResolved(_logger, sourceId, targetId, _zoneId);
            }
            else if (routeZone > 0)
            {
                if (targetId / 100 == _zoneId && routeZone != _zoneId)
                    _roaming.UpdateDeviceLocation(targetId, routeZone);
                QueueRadioCheck(routeZone, NextCorrelationId(), sourceId, targetId);
                LogResolved(_logger, sourceId, targetId, routeZone);
            }
            else
            {
                QueueFallback(sourceId, targetId, now);
                LogUnresolved(_logger, sourceId, targetId);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_stateLock) _activeResolutions--;
        }
    }

    private async Task<bool> ProbeHomeAsync(
        int sourceId,
        int targetId,
        int sourceHotspotId,
        CancellationToken token)
    {
        var key = PairKey(sourceId, targetId);
        var pending = new PendingProbe();
        if (!_pendingProbes.TryAdd(key, pending)) return false;
        try
        {
            var targetCount = CountProbeTargets(sourceHotspotId);
            if (targetCount == 0) return false;

            LogHomeProbeStarted(_logger, sourceId, targetId, targetCount);
            var burst = new byte[DmrBurstWriter.BurstLength];
            var packet = new byte[DmrBurstWriter.DmrdLength];
            for (var preamble = 5; preamble > 0; preamble--)
            {
                SendProbeBurstToRepeaters(
                    packet, burst, sourceId, targetId, sourceHotspotId, (byte)preamble, isRequest: false);
                await Task.Delay(BurstDelay, _timeProvider, token).ConfigureAwait(false);
            }

            SendProbeBurstToRepeaters(packet, burst, sourceId, targetId, sourceHotspotId, 0, isRequest: true);
            var timeoutTask = Task.Delay(HomeProbeResponseTimeout, _timeProvider, token);
            var completed = await Task.WhenAny(pending.Completion, timeoutTask).ConfigureAwait(false);
            if (completed == pending.Completion && await pending.Completion.ConfigureAwait(false))
            {
                LogHomeProbeAcknowledged(_logger, sourceId, targetId);
                return true;
            }

            LogHomeProbeTimedOut(_logger, sourceId, targetId);
            return false;
        }
        finally
        {
            _pendingProbes.TryRemove(key, out _);
        }
    }

    private int CountProbeTargets(int sourceHotspotId)
    {
        var count = 0;
        foreach (var repeater in _repeaters.GetRoutingSnapshot())
        {
            if (repeater.Id == sourceHotspotId || repeater.State != RepeaterState.LoggedIn ||
                repeater.EndPoint is null) continue;
            count++;
        }
        return count;
    }

    private void SendProbeBurstToRepeaters(
        Span<byte> packet,
        Span<byte> burst,
        int sourceId,
        int targetId,
        int sourceHotspotId,
        byte blocksToFollow,
        bool isRequest)
    {
        foreach (var repeater in _repeaters.GetRoutingSnapshot())
        {
            if (repeater.Id == sourceHotspotId || repeater.State != RepeaterState.LoggedIn ||
                repeater.EndPoint is not { } endpoint) continue;

            var colorCode = (byte)(repeater.Configuration?.ColorCode ?? 1);
            if (isRequest)
                DmrBurstWriter.WriteRadioCheckRequest(burst, sourceId, targetId, colorCode);
            else
                DmrBurstWriter.WriteCsbkPreamble(
                    burst, sourceId, targetId, blocksToFollow, colorCode, isDataContent: false);

            DmrBurstWriter.WriteDmrd(
                packet,
                burst,
                unchecked((byte)Interlocked.Increment(ref _packetSequence)),
                sourceId,
                targetId,
                repeater.Id,
                0x03,
                unchecked((uint)Interlocked.Increment(ref _streamSequence)));
            _sender.SendTo(packet, endpoint);
        }
    }

    private async Task<int> ResolveKnownRouteAsync(int sourceId, int targetId, CancellationToken token)
    {
        var homeZone = targetId / 100;
        if (homeZone == _zoneId)
        {
            if (!_router.TryGetHomeRouteCandidate(targetId, out var candidateZone, out _)) return 0;
            if (candidateZone == _zoneId) return _router.TryGetAnyLocalTarget(targetId, out _) ? _zoneId : 0;
            return await ConfirmZoneAsync(candidateZone, sourceId, targetId, token).ConfigureAwait(false)
                ? candidateZone
                : 0;
        }

        if (!_masters.TryGet(homeZone, out var homeMaster)) return 0;
        var response = await QueryZoneAsync(
            homeMaster.DataEndPoint, MeshRouteQueryScope.Home, sourceId, targetId, HomeTimeout, token).ConfigureAwait(false);
        if (response.Status != MeshRouteStatus.Found || response.RouteZone <= 0) return 0;
        if (response.RouteZone == homeZone) return homeZone;
        return await ConfirmZoneAsync(response.RouteZone, sourceId, targetId, token).ConfigureAwait(false)
            ? response.RouteZone
            : 0;
    }

    private async Task<bool> ConfirmZoneAsync(int zone, int sourceId, int targetId, CancellationToken token)
    {
        if (zone == _zoneId) return _router.TryGetAnyLocalTarget(targetId, out _);
        if (!_masters.TryGet(zone, out var master)) return false;
        var response = await QueryZoneAsync(
            master.DataEndPoint, MeshRouteQueryScope.Confirm, sourceId, targetId, HomeTimeout, token).ConfigureAwait(false);
        return response.Status == MeshRouteStatus.Found && response.RouteZone == zone;
    }

    private async Task<MeshControlMessage> QueryZoneAsync(
        Ipv4Endpoint endpoint,
        MeshRouteQueryScope scope,
        int sourceId,
        int targetId,
        TimeSpan timeout,
        CancellationToken token)
    {
        var queryId = NextCorrelationId();
        var pending = new PendingQuery();
        if (!_pendingQueries.TryAdd(queryId, pending)) return default;
        try
        {
            var query = new MeshControlMessage(
                MeshControlKind.RouteQuery, queryId, _zoneId, sourceId, targetId, Scope: scope);
            if (!_mesh.QueueControlMessage(in query, endpoint)) return default;
            var timeoutTask = Task.Delay(timeout, _timeProvider, token);
            var completed = await Task.WhenAny(pending.FirstResponse, timeoutTask).ConfigureAwait(false);
            return completed == pending.FirstResponse ? await pending.FirstResponse.ConfigureAwait(false) : default;
        }
        finally
        {
            _pendingQueries.TryRemove(queryId, out _);
        }
    }

    private async Task<int> ResolveWhoisAsync(int sourceId, int targetId, CancellationToken token)
    {
        var queryId = NextCorrelationId();
        var pending = new PendingQuery();
        if (!_pendingQueries.TryAdd(queryId, pending)) return 0;
        try
        {
            FanOutWhois(queryId, sourceId, targetId);
            await Task.Delay(WhoisRepeatDelay, _timeProvider, token).ConfigureAwait(false);
            FanOutWhois(queryId, sourceId, targetId);
            await Task.Delay(WhoisWindow - WhoisRepeatDelay, _timeProvider, token).ConfigureAwait(false);
            return pending.Best.Status == MeshRouteStatus.Found ? pending.Best.RouteZone : 0;
        }
        finally
        {
            _pendingQueries.TryRemove(queryId, out _);
        }
    }

    private void FanOutWhois(ulong queryId, int sourceId, int targetId)
    {
        var query = new MeshControlMessage(
            MeshControlKind.RouteQuery, queryId, _zoneId, sourceId, targetId, Scope: MeshRouteQueryScope.Whois);
        foreach (var peer in _masters.GetRoutingSnapshot()) _mesh.QueueControlMessage(in query, peer.DataEndPoint);
    }

    private void ProcessMeshControl(in MeshControlMessage message, Ipv4Endpoint remote)
    {
        switch (message.Kind)
        {
            case MeshControlKind.RouteQuery:
                ReplyToQuery(in message, remote);
                break;
            case MeshControlKind.RouteResult:
                if (_pendingQueries.TryGetValue(message.CorrelationId, out var pending)) pending.Add(in message);
                break;
            case MeshControlKind.RadioCheck:
                ExecuteRemoteRadioCheck(in message);
                break;
            case MeshControlKind.FallbackTransfer:
                AcceptFallback(in message, remote);
                break;
            case MeshControlKind.FallbackAccepted:
                _outbox.Remove(message.CorrelationId);
                break;
            case MeshControlKind.FallbackDeliver:
                DeliverRemoteFallback(in message);
                break;
            case MeshControlKind.FallbackResult:
                if (message.Status == MeshRouteStatus.Found) _outbox.Remove(message.CorrelationId);
                break;
        }
    }

    private void ReplyToQuery(in MeshControlMessage query, Ipv4Endpoint remote)
    {
        var found = false;
        var routeZone = 0;
        var observedAt = 0L;
        if (query.Scope == MeshRouteQueryScope.Home && query.TargetId / 100 == _zoneId)
            found = _router.TryGetHomeRouteCandidate(query.TargetId, out routeZone, out observedAt);
        else if (query.Scope is MeshRouteQueryScope.Confirm or MeshRouteQueryScope.Whois &&
                 _router.TryGetAnyLocalTarget(query.TargetId, out var local))
        {
            found = true;
            routeZone = _zoneId;
            observedAt = local.LastSeenTicks;
        }

        var result = new MeshControlMessage(
            MeshControlKind.RouteResult,
            query.CorrelationId,
            _zoneId,
            query.SourceId,
            query.TargetId,
            routeZone,
            observedAt,
            Scope: query.Scope,
            Status: found ? MeshRouteStatus.Found : MeshRouteStatus.NotFound);
        _mesh.QueueControlMessage(in result, remote);
    }

    private void QueueRadioCheck(int routeZone, ulong commandId, int sourceId, int targetId)
    {
        if (routeZone == _zoneId)
        {
            _ = SendRadioCheckAsync(commandId, sourceId, targetId, CancellationToken.None);
            return;
        }
        if (!_masters.TryGet(routeZone, out var master)) return;
        var command = new MeshControlMessage(
            MeshControlKind.RadioCheck, commandId, _zoneId, sourceId, targetId, routeZone);
        _mesh.QueueControlMessage(in command, master.DataEndPoint);
    }

    private void ExecuteRemoteRadioCheck(in MeshControlMessage message)
    {
        var now = _timeProvider.GetUtcNow().Ticks;
        if (!TryRememberCommand(message.CorrelationId, now)) return;
        _ = SendRadioCheckAsync(message.CorrelationId, message.SourceId, message.TargetId, CancellationToken.None);
    }

    private bool TryRememberCommand(ulong correlationId, long now)
    {
        lock (_stateLock)
        {
            if (_receivedCommands.ContainsKey(correlationId)) return false;
            if (_receivedCommands.Count >= _maxPending * 2) RemoveOldestCommand();
            _receivedCommands[correlationId] = now + ReplayRetention.Ticks;
        }
        return true;
    }

    private async Task SendRadioCheckAsync(ulong commandId, int sourceId, int targetId, CancellationToken token)
    {
        if (!_router.TryGetAnyLocalTarget(targetId, out var route)) return;
        var burst = new byte[DmrBurstWriter.BurstLength];
        var packet = new byte[DmrBurstWriter.DmrdLength];
        for (var preamble = 5; preamble > 0; preamble--)
        {
            DmrBurstWriter.WriteCsbkPreamble(
                burst, sourceId, targetId, (byte)preamble, route.ColorCode, isDataContent: false);
            SendControlBurst(packet, burst, sourceId, targetId, in route);
            await Task.Delay(BurstDelay, _timeProvider, token).ConfigureAwait(false);
        }
        DmrBurstWriter.WriteRadioCheckRequest(burst, sourceId, targetId, route.ColorCode);
        SendControlBurst(packet, burst, sourceId, targetId, in route);
    }

    private void SendControlBurst(
        Span<byte> packet,
        ReadOnlySpan<byte> burst,
        int sourceId,
        int targetId,
        in RadioTargetRoute route)
    {
        DmrBurstWriter.WriteDmrd(
            packet,
            burst,
            unchecked((byte)Interlocked.Increment(ref _packetSequence)),
            sourceId,
            targetId,
            route.HotspotId,
            0x03,
            unchecked((uint)Interlocked.Increment(ref _streamSequence)));
        _sender.SendTo(packet, route.EndPoint);
    }

    private void QueueFallback(int sourceId, int targetId, long nowTicks)
    {
        if (!_settings.FallbackSdsEnabled) return;
        var entry = new RadioCheckOutboxEntry
        {
            MessageId = NextCorrelationId(),
            OriginZone = _zoneId,
            HomeZone = targetId / 100,
            SourceId = sourceId,
            TargetId = targetId,
            CreatedAtTicks = nowTicks,
            ExpiresAtTicks = nowTicks + TimeSpan.FromHours(_settings.FallbackTtlHours).Ticks
        };
        if (!_outbox.TryAdd(in entry)) Interlocked.Increment(ref _droppedFallbacks);
    }

    private void ProcessOutbox()
    {
        if (!_settings.FallbackSdsEnabled) return;
        var now = _timeProvider.GetUtcNow().Ticks;
        foreach (var entry in _outbox.Snapshot(now))
        {
            if (entry.HomeZone != _zoneId)
            {
                if (now - entry.LastAttemptTicks < TransferRetryDelay.Ticks ||
                    !_masters.TryGet(entry.HomeZone, out var home)) continue;
                var transfer = ToFallbackMessage(MeshControlKind.FallbackTransfer, entry, _zoneId);
                if (_mesh.QueueControlMessage(in transfer, home.DataEndPoint)) _outbox.MarkAttempt(entry.MessageId, now);
                continue;
            }

            if (now - entry.LastAttemptTicks < DeliveryRetryDelay.Ticks) continue;
            if (_router.TryGetAnyLocalTarget(entry.TargetId, out var local))
            {
                var request = new FallbackSdsRequest(entry.MessageId, _zoneId, entry.SourceId, entry.TargetId, local);
                if (_fallbackSds.TryQueue(in request)) _outbox.MarkAttempt(entry.MessageId, now);
            }
            else if (_roaming.TryGetRoamedDeviceZone(entry.TargetId, out var zone) &&
                     _masters.TryGet(zone, out var serving))
            {
                var delivery = ToFallbackMessage(MeshControlKind.FallbackDeliver, entry, _zoneId);
                if (_mesh.QueueControlMessage(in delivery, serving.DataEndPoint)) _outbox.MarkAttempt(entry.MessageId, now);
            }
        }
    }

    private void AcceptFallback(in MeshControlMessage message, Ipv4Endpoint remote)
    {
        if (message.TargetId / 100 != _zoneId || message.ExpiresAtTicks <= _timeProvider.GetUtcNow().Ticks) return;
        var entry = new RadioCheckOutboxEntry
        {
            MessageId = message.CorrelationId,
            OriginZone = message.OriginZone,
            HomeZone = _zoneId,
            SourceId = message.SourceId,
            TargetId = message.TargetId,
            CreatedAtTicks = message.CreatedAtTicks,
            ExpiresAtTicks = message.ExpiresAtTicks
        };
        if (!_outbox.TryAdd(in entry)) return;
        var accepted = ToFallbackMessage(MeshControlKind.FallbackAccepted, entry, _zoneId);
        _mesh.QueueControlMessage(in accepted, remote);
    }

    private void DeliverRemoteFallback(in MeshControlMessage message)
    {
        if (!TryRememberCommand(message.CorrelationId, _timeProvider.GetUtcNow().Ticks)) return;
        if (!_router.TryGetAnyLocalTarget(message.TargetId, out var local)) return;
        var request = new FallbackSdsRequest(
            message.CorrelationId, message.OriginZone, message.SourceId, message.TargetId, local);
        _fallbackSds.TryQueue(in request);
    }

    private void ProcessDelivery(in FallbackSdsDelivery delivery)
    {
        if (delivery.OwnerZone == _zoneId)
        {
            if (delivery.Delivered) _outbox.Remove(delivery.MessageId);
            return;
        }
        if (!_masters.TryGet(delivery.OwnerZone, out var owner)) return;
        var result = new MeshControlMessage(
            MeshControlKind.FallbackResult,
            delivery.MessageId,
            _zoneId,
            0,
            0,
            Status: delivery.Delivered ? MeshRouteStatus.Found : MeshRouteStatus.NotFound);
        _mesh.QueueControlMessage(in result, owner.DataEndPoint);
    }

    private static MeshControlMessage ToFallbackMessage(
        MeshControlKind kind,
        RadioCheckOutboxEntry entry,
        int senderZone) => new(
        kind,
        entry.MessageId,
        senderZone,
        entry.SourceId,
        entry.TargetId,
        CreatedAtTicks: entry.CreatedAtTicks,
        ExpiresAtTicks: entry.ExpiresAtTicks);

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
