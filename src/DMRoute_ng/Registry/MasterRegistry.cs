using System.Collections.Concurrent;
using System.Net;

namespace DMRoute_ng.Registry;

public sealed class MasterPeer(int zoneId, IPEndPoint dataEndPoint)
{
    public int ZoneId { get; } = zoneId;
    public IPEndPoint DataEndPoint { get; } = dataEndPoint;
    public long LastSeenTicks = DateTime.UtcNow.Ticks;
}

public sealed class MasterRegistry
{
    private readonly ConcurrentDictionary<int, MasterPeer> _peers = new();

    public void AddOrUpdate(int zoneId, IPEndPoint dataEndPoint)
    {
        if (_peers.TryGetValue(zoneId, out var peer))
        {
            peer.DataEndPoint.Address = dataEndPoint.Address;
            peer.DataEndPoint.Port = dataEndPoint.Port;
            Volatile.Write(ref peer.LastSeenTicks, DateTime.UtcNow.Ticks);
        }
        else
        {
            _peers.TryAdd(zoneId, new MasterPeer(zoneId, dataEndPoint));
        }
    }

    public bool TryGet(int zoneId, out MasterPeer peer) => _peers.TryGetValue(zoneId, out peer!);
    
    public ConcurrentDictionary<int, MasterPeer> GetAll() => _peers;
}