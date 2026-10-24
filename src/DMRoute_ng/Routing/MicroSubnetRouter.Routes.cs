using System.Buffers.Binary;
using DMRoute_ng.Coding;
using DMRoute_ng.Core;
using DMRoute_ng.Registry;
using DMRoute_ng.Services;
using DMRoute_ng.Types;
using Microsoft.Extensions.Logging;

namespace DMRoute_ng.Routing;

public sealed partial class MicroSubnetRouter
{
    private void LearnLocalRoute(int sourceId, int repeaterId)
    {
        var route = new LocalDeviceRoute(repeaterId, DateTime.UtcNow.Ticks);
        lock (_localRoutesLock)
        {
            if (_localDeviceRouting.TryGetValue(sourceId, out _))
            {
                _localDeviceRouting[sourceId] = route;
                return;
            }

            if (_localDeviceRouting.Count >= _maxLocalDeviceRoutes)
            {
                Interlocked.Increment(ref _droppedLocalRouteStates);
                return;
            }
            _localDeviceRouting.Add(sourceId, route);
        }
    }

    public bool TryGetHomeRouteCandidate(int deviceId, out int routeZone, out long lastSeenTicks)
    {
        routeZone = 0;
        lastSeenTicks = 0;
        if (deviceId / 100 != _masterZoneId) return false;

        if (TryGetLocalHomeTarget(deviceId, out var local))
        {
            routeZone = _masterZoneId;
            lastSeenTicks = local.LastSeenTicks;
        }

        if (_roamingRegistry.TryGetRoamedDevice(deviceId, out var roamingZone, out var roamingSeen) &&
            roamingSeen > lastSeenTicks)
        {
            routeZone = roamingZone;
            lastSeenTicks = roamingSeen;
        }

        return routeZone != 0;
    }

    private bool TryGetLocalHomeTarget(int deviceId, out RadioTargetRoute route)
    {
        LocalDeviceRoute state;
        lock (_localRoutesLock)
        {
            if (!_localDeviceRouting.TryGetValue(deviceId, out state) ||
                state.LastSeenTicks < DateTime.UtcNow.AddMinutes(-15).Ticks)
            {
                route = default;
                return false;
            }
        }

        if (_registry.TryGetExisting(state.HotspotId, out var repeater) &&
            repeater.State == RepeaterState.LoggedIn && repeater.EndPoint is { } endpoint)
        {
            route = new RadioTargetRoute(
                state.HotspotId, endpoint, (byte)(repeater.Configuration?.ColorCode ?? 1), state.LastSeenTicks);
            return true;
        }

        route = default;
        return false;
    }

    private bool TryGetLocalGuestTarget(int deviceId, out RadioTargetRoute route)
    {
        if (_roamingRegistry.TryGetLocalGuestRoute(deviceId, out var hotspotId, out var endpoint, out var lastSeen) &&
            _registry.TryGetExisting(hotspotId, out var repeater) && repeater.State == RepeaterState.LoggedIn &&
            repeater.EndPoint == endpoint)
        {
            route = new RadioTargetRoute(
                hotspotId, endpoint, (byte)(repeater.Configuration?.ColorCode ?? 1), lastSeen);
            return true;
        }

        route = default;
        return false;
    }
}
