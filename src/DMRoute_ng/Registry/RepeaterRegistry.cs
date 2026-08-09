using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using DMRoute_ng.Types;

namespace DMRoute_ng.Registry;

public sealed class RepeaterRegistry(ILogger<RepeaterRegistry> logger, int masterZoneId, string sharedPsk)
    : BackgroundService
{
    private readonly ConcurrentDictionary<int, Repeater> _repeaters = new();

    // Zero-Allocation Lookup & Dynamisches Whitelisting
    public bool TryGet(int repeaterId, out Repeater repeater)
    {
        if (_repeaters.TryGetValue(repeaterId, out var existing))
        {
            repeater = existing;
            return true;
        }

        // Dynamisches Whitelisting: Gehört die ID mathematisch zu unserer Zone?
        // Beispiel: Zone 101 -> Erlaubt sind IDs wie 1010001 (1010001 / 10000 = 101)
        if (repeaterId / 10000 == masterZoneId)
        {
            // Individueller PSK: Master-PSK + Repeater-ID (z. B. "s3cr37w0rd1000001")
            // Die String-Allokation passiert exakt 1x pro neuem Repeater und belastet den Hot-Path nicht.
            var uniquePsk = $"{sharedPsk}{repeaterId}";
            
            // Allokation findet exakt 1x pro neuem, legitimen Hotspot statt
            repeater = new Repeater(repeaterId, uniquePsk, RepeaterState.Disconnected, null);
            _repeaters.TryAdd(repeaterId, repeater);
            return true;
        }

        repeater = null!;
        return false;
    }

    // Liefert das Dictionary zurück, damit Caller den Struct-Enumerator (Zero-Alloc) nutzen können
    public ConcurrentDictionary<int, Repeater> GetAll() => _repeaters;
    
    // ReSharper disable once CognitiveComplexity
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            long cutoffTicks = DateTime.UtcNow.AddSeconds(-45).Ticks;

            // foreach auf ConcurrentDictionary nutzt implizit einen Struct-Enumerator -> 0 Bytes GC Allokation
            foreach (var kvp in _repeaters)
            {
                var repeater = kvp.Value;
                if (repeater.State == RepeaterState.LoggedIn)
                {
                    var lastPing = Volatile.Read(ref repeater.LastPingTicks);
                    
                    if (lastPing > 0 && lastPing < cutoffTicks)
                    {
                        var secondsIdle = (DateTime.UtcNow.Ticks - lastPing) / TimeSpan.TicksPerSecond;
                        logger.LogInformation("Timeout für Repeater {Id} nach {Seconds}s Inaktivität. Setze auf Disconnected", repeater.Id, secondsIdle);
                        
                        repeater.State = RepeaterState.Disconnected;
                        Volatile.Write(ref repeater.LastPingTicks, 0);
                    }
                }
            }
        }
    }
}