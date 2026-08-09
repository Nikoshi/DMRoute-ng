using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DMRoute_ng.Registry;

public sealed class ForeignDeviceEntry(int deviceId, int currentZoneId)
{
    public int DeviceId { get; } = deviceId;
    public int CurrentZoneId { get; set; } = currentZoneId;
    public long LastSeenTicks = DateTime.UtcNow.Ticks;
}

public sealed class LocalGuestDeviceEntry(int deviceId, IPEndPoint hotspotEndPoint)
{
    public int DeviceId { get; } = deviceId;
    public IPEndPoint HotspotEndPoint { get; set; } = hotspotEndPoint;
    public long LastSeenTicks = DateTime.UtcNow.Ticks;
}

public sealed class RoamingRegistry(ILogger<RoamingRegistry> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<int, ForeignDeviceEntry> _roamingHomeDevices = new();
    private readonly ConcurrentDictionary<int, LocalGuestDeviceEntry> _localGuestDevices = new();

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
                if (entry.CurrentZoneId != foreignZoneId)
                {
                    isNewOrChanged = true;
                }
                entry.CurrentZoneId = foreignZoneId;
                Volatile.Write(ref entry.LastSeenTicks, DateTime.UtcNow.Ticks);
                return entry;
            });
        
        if (isNewOrChanged)
        {
            logger.LogInformation("Roaming: Heimat-Gerät {DeviceId} roamt in Zone {ZoneId}", deviceId, foreignZoneId);
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

    public void TrackLocalGuest(int deviceId, IPEndPoint hotspotEndPoint)
    {
        _localGuestDevices.AddOrUpdate(
            deviceId,
            id => new LocalGuestDeviceEntry(id, hotspotEndPoint),
            (id, entry) =>
            {
                entry.HotspotEndPoint = hotspotEndPoint;
                Volatile.Write(ref entry.LastSeenTicks, DateTime.UtcNow.Ticks);
                return entry;
            });
    }

    public bool TryGetLocalGuestEndpoint(int deviceId, out IPEndPoint? hotspotEndPoint)
    {
        if (_localGuestDevices.TryGetValue(deviceId, out var entry))
        {
            hotspotEndPoint = entry.HotspotEndPoint;
            return true;
        }

        hotspotEndPoint = null;
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            // Timeout nach 15 Minuten Inaktivität
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

            foreach (var kvp in _localGuestDevices)
            {
                if (Volatile.Read(ref kvp.Value.LastSeenTicks) < cutoffTicks)
                {
                    _localGuestDevices.TryRemove(kvp.Key, out _);
                }
            }
        }
    }
}