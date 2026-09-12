using System.Collections.Concurrent;
using DMRoute_ng.Types;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DMRoute_ng.Registry;

public sealed class ForeignDeviceEntry(int deviceId, int currentZoneId)
{
    public int DeviceId { get; } = deviceId;
    public int CurrentZoneId { get; set; } = currentZoneId;
    public long LastSeenTicks = DateTime.UtcNow.Ticks;
}

public readonly record struct LocalGuestSnapshot(
    int DeviceId,
    int HotspotId,
    long ActiveSinceTicks,
    long LastSeenTicks);

public readonly record struct AwayDeviceSnapshot(
    int DeviceId,
    int CurrentZoneId,
    long LastSeenTicks);

public sealed class RoamingRegistry(
    ILogger<RoamingRegistry> logger,
    int maxLocalGuests = 8192) : BackgroundService
{
    private struct LocalGuestState
    {
        public int DeviceId;
        public int HotspotId;
        public Ipv4Endpoint HotspotEndPoint;
        public long ActiveSinceTicks;
        public long LastSeenTicks;
        public bool Active;
    }

    private readonly ConcurrentDictionary<int, ForeignDeviceEntry> _roamingHomeDevices =
        new(Environment.ProcessorCount, maxLocalGuests);
    private readonly Dictionary<int, int> _localGuestSlots = new(maxLocalGuests);
    private readonly LocalGuestState[] _localGuests = new LocalGuestState[maxLocalGuests];
    private readonly object _localGuestLock = new();
    private long _droppedLocalGuestStates;

    public long DroppedLocalGuestStates => Interlocked.Read(ref _droppedLocalGuestStates);
    public int MaxLocalGuests => _localGuests.Length;
    public int MaxAwayDevices => _localGuests.Length;

    public void UpdateDeviceLocation(int deviceId, int foreignZoneId)
    {
        var now = DateTime.UtcNow.Ticks;
        if (_roamingHomeDevices.TryGetValue(deviceId, out var entry))
        {
            var changed = entry.CurrentZoneId != foreignZoneId;
            entry.CurrentZoneId = foreignZoneId;
            Volatile.Write(ref entry.LastSeenTicks, now);
            if (changed) logger.LogInformation("Roaming: Heimat-Gerät {DeviceId} roamt in Zone {ZoneId}", deviceId, foreignZoneId);
            return;
        }

        if (_roamingHomeDevices.Count >= MaxAwayDevices)
        {
            Interlocked.Increment(ref _droppedLocalGuestStates);
            return;
        }

        if (_roamingHomeDevices.TryAdd(deviceId, new ForeignDeviceEntry(deviceId, foreignZoneId)))
            logger.LogInformation("Roaming: Heimat-Gerät {DeviceId} roamt in Zone {ZoneId}", deviceId, foreignZoneId);
    }

    public bool TryGetRoamedDeviceZone(int deviceId, out int foreignZoneId)
    {
        if (_roamingHomeDevices.TryGetValue(deviceId, out var entry))
        {
            foreignZoneId = entry.CurrentZoneId;
            return true;
        }

        foreignZoneId = 0;
        return false;
    }

    public void TrackLocalGuest(int deviceId, int hotspotId, Ipv4Endpoint hotspotEndPoint)
    {
        var now = DateTime.UtcNow.Ticks;
        lock (_localGuestLock)
        {
            if (_localGuestSlots.TryGetValue(deviceId, out var existingSlot))
            {
                ref var existing = ref _localGuests[existingSlot];
                existing.HotspotId = hotspotId;
                existing.HotspotEndPoint = hotspotEndPoint;
                existing.LastSeenTicks = now;
                return;
            }

            if (_localGuestSlots.Count >= _localGuests.Length)
            {
                Interlocked.Increment(ref _droppedLocalGuestStates);
                return;
            }

            for (var slot = 0; slot < _localGuests.Length; slot++)
            {
                if (_localGuests[slot].Active) continue;
                _localGuests[slot] = new LocalGuestState
                {
                    DeviceId = deviceId,
                    HotspotId = hotspotId,
                    HotspotEndPoint = hotspotEndPoint,
                    ActiveSinceTicks = now,
                    LastSeenTicks = now,
                    Active = true
                };
                _localGuestSlots.Add(deviceId, slot);
                break;
            }
        }
    }

    public int CopyLocalGuests(Span<LocalGuestSnapshot> destination)
    {
        var count = 0;
        lock (_localGuestLock)
        {
            for (var slot = 0; slot < _localGuests.Length && count < destination.Length; slot++)
            {
                ref readonly var guest = ref _localGuests[slot];
                if (!guest.Active) continue;
                destination[count++] = new LocalGuestSnapshot(
                    guest.DeviceId, guest.HotspotId, guest.ActiveSinceTicks, guest.LastSeenTicks);
            }
        }
        return count;
    }

    public int CopyAwayDevices(Span<AwayDeviceSnapshot> destination)
    {
        var count = 0;
        foreach (var pair in _roamingHomeDevices)
        {
            if (count == destination.Length) break;
            var entry = pair.Value;
            destination[count++] = new AwayDeviceSnapshot(
                entry.DeviceId, entry.CurrentZoneId, Volatile.Read(ref entry.LastSeenTicks));
        }
        return count;
    }

    public bool TryGetLocalGuestEndpoint(int deviceId, out Ipv4Endpoint hotspotEndPoint)
    {
        lock (_localGuestLock)
        {
            if (_localGuestSlots.TryGetValue(deviceId, out var slot))
            {
                hotspotEndPoint = _localGuests[slot].HotspotEndPoint;
                return true;
            }
        }

        hotspotEndPoint = default;
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            long cutoffTicks = DateTime.UtcNow.AddMinutes(-15).Ticks;

            foreach (var kvp in _roamingHomeDevices)
            {
                if (Volatile.Read(ref kvp.Value.LastSeenTicks) < cutoffTicks)
                {
                    if (_roamingHomeDevices.TryRemove(kvp.Key, out _))
                    {
                        logger.LogInformation("Roaming: Eintrag für {DeviceId} abgelaufen", kvp.Key);
                    }
                }
            }

            lock (_localGuestLock)
            {
                for (var slot = 0; slot < _localGuests.Length; slot++)
                {
                    ref var guest = ref _localGuests[slot];
                    if (!guest.Active || guest.LastSeenTicks >= cutoffTicks) continue;
                    _localGuestSlots.Remove(guest.DeviceId);
                    guest = default;
                }
            }
        }
    }
}
