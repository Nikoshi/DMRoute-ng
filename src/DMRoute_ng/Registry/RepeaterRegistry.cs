using System.Collections.Concurrent;
using DMRoute_ng.Types;

namespace DMRoute_ng.Registry;

public class RepeaterRegistry
{
    private readonly ConcurrentDictionary<int, Repeater> _repeaters = new();

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
    }

    public bool TryGet(int id, out Repeater? repeater) => _repeaters.TryGetValue(id, out repeater);
    
    public void Remove(int id) => _repeaters.TryRemove(id, out _);
    
    public IEnumerable<Repeater> GetAll() => _repeaters.Values;
}