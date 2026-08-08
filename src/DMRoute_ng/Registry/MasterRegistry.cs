using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DMRoute_ng.Registry;

public sealed class MasterPeer(int zoneId, IPEndPoint dataEndPoint)
{
    public int ZoneId { get; } = zoneId;
    public IPEndPoint DataEndPoint { get; } = dataEndPoint;
    public long LastSeenTicks = DateTime.UtcNow.Ticks;
}

public sealed class MasterRegistry(ILogger<MasterRegistry> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<int, MasterPeer> _peers = new();

    // Gibt true zurück, wenn die Zone neu ist
    public bool AddOrUpdate(int zoneId, IPEndPoint dataEndPoint)
    {
        if (_peers.TryGetValue(zoneId, out var peer))
        {
            peer.DataEndPoint.Address = dataEndPoint.Address;
            peer.DataEndPoint.Port = dataEndPoint.Port;
            Volatile.Write(ref peer.LastSeenTicks, DateTime.UtcNow.Ticks);
            return false; 
        }
        
        _peers.TryAdd(zoneId, new MasterPeer(zoneId, dataEndPoint));
        return true; 
    }

    public bool TryGet(int zoneId, out MasterPeer peer) => _peers.TryGetValue(zoneId, out peer!);
    
    // Legacy für Debugging / Status-Websites etc.
    public ConcurrentDictionary<int, MasterPeer> GetAll() => _peers;

    /// <summary>
    /// Zero-Allocation Methode für Talkgroup 1 (Global).
    /// Füllt den übergebenen Span mit allen aktiven Endpoints und gibt die Anzahl zurück.
    /// </summary>
    public int GetActiveEndpoints(Span<IPEndPoint> buffer)
    {
        var count = 0;
        foreach (var kvp in _peers)
        {
            if (count >= buffer.Length) break;
            buffer[count++] = kvp.Value.DataEndPoint;
        }
        return count;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            // 90 Sekunden Timeout (3 verpasste Beacons)
            long cutoffTicks = DateTime.UtcNow.AddSeconds(-90).Ticks;

            foreach (var kvp in _peers)
            {
                if (Volatile.Read(ref kvp.Value.LastSeenTicks) < cutoffTicks)
                {
                    if (_peers.TryRemove(kvp.Key, out _))
                    {
                        logger.LogWarning("Mesh: Zone {ZoneId} Timeout (Offline). Aus Routing entfernt.", kvp.Key);
                    }
                }
            }
        }
    }
}