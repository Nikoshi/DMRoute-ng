using System.Collections.Concurrent;
using System.Threading.Channels;
using DMRoute_ng.Integration;
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

public sealed class RoamingRegistry(
    ILogger<RoamingRegistry> logger,
    ChannelWriter<MqttEvent> eventWriter,
    int maxLocalGuests = 8192) : BackgroundService
{
    private struct LocalGuestState
    {
        public int DeviceId;
        public Ipv4Endpoint HotspotEndPoint;
        public long LastSeenTicks;
        public bool Active;
    }

    private readonly ConcurrentDictionary<int, ForeignDeviceEntry> _roamingHomeDevices = new();
    private readonly Dictionary<int, int> _localGuestSlots = new(maxLocalGuests);
    private readonly LocalGuestState[] _localGuests = new LocalGuestState[maxLocalGuests];
    private readonly object _localGuestLock = new();
    private long _droppedLocalGuestStates;

    public long DroppedLocalGuestStates => Interlocked.Read(ref _droppedLocalGuestStates);

    public void UpdateDeviceLocation(int deviceId, int foreignZoneId)
    {
        bool isNewOrChanged = false;

        _roamingHomeDevices.AddOrUpdate(
            deviceId,
            id => 
            {
                isNewOrChanged = true;
                return new ForeignDeviceEntry(id, foreignZoneId);
            },
            (id, entry) =>
            {
                if (entry.CurrentZoneId != foreignZoneId) isNewOrChanged = true;
                entry.CurrentZoneId = foreignZoneId;
                Volatile.Write(ref entry.LastSeenTicks, DateTime.UtcNow.Ticks);
                return entry;
            });
        
        if (isNewOrChanged)
        {
            logger.LogInformation("Roaming: Heimat-Gerät {DeviceId} roamt in Zone {ZoneId}", deviceId, foreignZoneId);
            eventWriter.TryWrite(new MqttEvent(0x12, deviceId, foreignZoneId));
        }
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

    public void TrackLocalGuest(int deviceId, Ipv4Endpoint hotspotEndPoint)
    {
        var isNew = false;
        lock (_localGuestLock)
        {
            if (_localGuestSlots.TryGetValue(deviceId, out var existingSlot))
            {
                ref var existing = ref _localGuests[existingSlot];
                existing.HotspotEndPoint = hotspotEndPoint;
                existing.LastSeenTicks = DateTime.UtcNow.Ticks;
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
                    HotspotEndPoint = hotspotEndPoint,
                    LastSeenTicks = DateTime.UtcNow.Ticks,
                    Active = true
                };
                _localGuestSlots.Add(deviceId, slot);
                isNew = true;
                break;
            }
        }

        if (isNew)
        {
            eventWriter.TryWrite(new MqttEvent(0x10, deviceId));
        }
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
                        eventWriter.TryWrite(new MqttEvent(0x13, kvp.Key));
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
                    eventWriter.TryWrite(new MqttEvent(0x11, guest.DeviceId));
                    guest = default;
                }
            }
        }
    }
}
