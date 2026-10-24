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
        // Eine Correlation-ID darf innerhalb des Replay-Fensters höchstens eine
        // Funktransaktion auslösen, auch wenn Mesh-Pakete wiederholt eintreffen.
        lock (_stateLock)
        {
            if (_receivedCommands.ContainsKey(correlationId)) return false;
            if (_receivedCommands.Count >= _maxPending * 2) RemoveOldestCommand();
            _receivedCommands[correlationId] = now + ReplayRetention.Ticks;
        }
        return true;
    }
}
