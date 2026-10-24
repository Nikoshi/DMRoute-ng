using System.Collections.Concurrent;
using DMRoute_ng.Types;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DMRoute_ng.Registry;

public sealed class MasterPeer(int zoneId, Ipv4Endpoint dataEndPoint)
{
    private long _dataEndPoint = (long)dataEndPoint.PackedValue;

    public int ZoneId { get; } = zoneId;
    public Ipv4Endpoint DataEndPoint
    {
        get => Ipv4Endpoint.FromPackedValue((ulong)Volatile.Read(ref _dataEndPoint));
        set => Volatile.Write(ref _dataEndPoint, (long)value.PackedValue);
    }
    public long LastSeenTicks = DateTime.UtcNow.Ticks;
}

public sealed class MasterRegistry(ILogger<MasterRegistry> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<int, MasterPeer> _peers = new();
    private volatile MasterPeer[] _routingSnapshot = [];

    /// <summary>
    /// Aktualisiert Endpunkt und Aktivitätszeit einer Zone und gibt <see langword="true"/>
    /// zurück, wenn die Zone neu aufgenommen wurde.
    /// </summary>
    public bool AddOrUpdate(int zoneId, Ipv4Endpoint dataEndPoint)
    {
        if (_peers.TryGetValue(zoneId, out var peer))
        {
            peer.DataEndPoint = dataEndPoint;
            Volatile.Write(ref peer.LastSeenTicks, DateTime.UtcNow.Ticks);
            return false;
        }

        _peers.TryAdd(zoneId, new MasterPeer(zoneId, dataEndPoint));
        RefreshRoutingSnapshot();
        return true;
    }

    public bool TryGet(int zoneId, out MasterPeer peer) => _peers.TryGetValue(zoneId, out peer!);

    /// <summary>
    /// Liefert die live verwendete Peer-Tabelle für Diagnosezwecke.
    /// Aufrufer dürfen sie nicht verändern.
    /// </summary>
    public ConcurrentDictionary<int, MasterPeer> GetAll() => _peers;

    /// <summary>
    /// Kopiert aktive Master-Endpunkte ohne Zwischenallokation in den Zielspan.
    /// </summary>
    /// <returns>Anzahl der geschriebenen Endpunkte; überzählige Peers werden ausgelassen.</returns>
    public int GetActiveEndpoints(Span<Ipv4Endpoint> buffer)
    {
        var count = 0;
        foreach (var peer in _routingSnapshot)
        {
            if (count >= buffer.Length) break;
            buffer[count++] = peer.DataEndPoint;
        }
        return count;
    }

    /// <summary>
    /// Liefert den unveränderlichen Routing-Snapshot. Der Span darf die synchrone
    /// Verarbeitung des Aufrufers nicht verlassen.
    /// </summary>
    public ReadOnlySpan<MasterPeer> GetRoutingSnapshot() => _routingSnapshot;

    private void RefreshRoutingSnapshot()
    {
        var snapshot = new MasterPeer[_peers.Count];
        var count = 0;
        foreach (var pair in _peers)
        {
            if (count == snapshot.Length) Array.Resize(ref snapshot, count + 1);
            snapshot[count++] = pair.Value;
        }
        if (count != snapshot.Length) Array.Resize(ref snapshot, count);
        _routingSnapshot = snapshot;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            // 90 Sekunden entsprechen drei ausgebliebenen Discovery-Beacons.
            long cutoffTicks = DateTime.UtcNow.AddSeconds(-90).Ticks;

            foreach (var kvp in _peers)
            {
                if (Volatile.Read(ref kvp.Value.LastSeenTicks) < cutoffTicks)
                {
                    if (_peers.TryRemove(kvp.Key, out _))
                    {
                        RefreshRoutingSnapshot();
                        logger.LogWarning("Mesh: Zone {ZoneId} Timeout (Offline). Aus Routing entfernt.", kvp.Key);
                    }
                }
            }
        }
    }
}
