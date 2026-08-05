using System.Collections.Concurrent;
using DMRoute_ng.Types;

namespace DMRoute_ng.Registry;

public class RepeaterRegistry
{
    private readonly ConcurrentDictionary<int, Repeater> _repeaters = new();
    
    private volatile Repeater[] _routingTable = [];
    
    private void UpdateRoutingTable()
    {
        _routingTable = _repeaters.Values
            .Where(r => r.State == RepeaterState.LoggedIn && r.EndPoint != null)
            .ToArray();
    }
    
    public void AddOrUpdate(Repeater repeater)
    {
        _repeaters.AddOrUpdate(repeater.Id, repeater, (_, existing) => 
        {
            existing.State = repeater.State;
            existing.Configuration = repeater.Configuration ?? existing.Configuration;
            existing.EndPoint = repeater.EndPoint ?? existing.EndPoint;
            existing.LastPing = repeater.LastPing ?? existing.LastPing;
            return existing;
        });
        
        UpdateRoutingTable();
    }
   
    public bool TryGet(int id, out Repeater? repeater) => _repeaters.TryGetValue(id, out repeater);
    
    public void Remove(int id) => _repeaters.TryRemove(id, out _);
    
    public ReadOnlySpan<Repeater> GetActivePeers() => _routingTable;
}