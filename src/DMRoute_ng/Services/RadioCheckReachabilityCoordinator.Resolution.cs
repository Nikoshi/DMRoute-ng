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

public sealed partial class RadioCheckReachabilityCoordinator
{
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
        // Reihenfolge ist Teil des Funklastvertrags: vorhandene lokale Route,
        // einmalige Heimat-Probe, bekannte Mesh-Route und zuletzt WHOIS-Fan-out.
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
            // Der Quell-Hotspot wird ausgelassen, damit die vom Teilnehmer kommende
            // Anfrage nicht unmittelbar als Master-Probe dorthin zurückgespiegelt wird.
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
}
