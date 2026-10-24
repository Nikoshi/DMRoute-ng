using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using DMRoute_ng.Types;

namespace DMRoute_ng.Registry;

public sealed class RepeaterRegistry(ILogger<RepeaterRegistry> logger, int masterZoneId, string sharedPsk)
    : BackgroundService
{
    private readonly ConcurrentDictionary<int, Repeater> _repeaters = new();
    private volatile Repeater[] _routingSnapshot = [];

    /// <summary>
    /// Sucht einen Hotspot oder legt ihn während der RPTL-Anmeldung an, sofern seine
    /// ID zur lokalen Zone gehört.
    /// </summary>
    public bool TryGet(int repeaterId, out Repeater repeater)
    {
        if (_repeaters.TryGetValue(repeaterId, out var existing))
        {
            repeater = existing;
            return true;
        }

        // Die führenden Ziffern der siebenstelligen Hotspot-ID bilden die Zone.
        if (repeaterId / 10000 == masterZoneId)
        {
            // Der individuelle PSK wird einmalig bei der Anmeldung erzeugt, außerhalb
            // der Paketverarbeitung nach erfolgreichem Login.
            var uniquePsk = $"{sharedPsk}{repeaterId}";

            repeater = new Repeater(repeaterId, uniquePsk, RepeaterState.Disconnected, null);
            _repeaters.TryAdd(repeaterId, repeater);
            return true;
        }

        repeater = null!;
        return false;
    }

    public bool TryGetExisting(int repeaterId, out Repeater repeater) =>
        _repeaters.TryGetValue(repeaterId, out repeater!);

    /// <summary>
    /// Liefert die live verwendete Hotspot-Tabelle für Diagnosezwecke.
    /// Aufrufer dürfen sie nicht verändern.
    /// </summary>
    public ConcurrentDictionary<int, Repeater> GetAll() => _repeaters;

    /// <summary>
    /// Liefert den Routing-Snapshot ohne Allokation. Der Span darf keinen
    /// <see langword="await"/>-Übergang überschreiten.
    /// </summary>
    public ReadOnlySpan<Repeater> GetRoutingSnapshot() => _routingSnapshot;

    public void RefreshRoutingSnapshot()
    {
        var snapshot = new Repeater[_repeaters.Count];
        var count = 0;
        foreach (var pair in _repeaters)
        {
            if (count == snapshot.Length) Array.Resize(ref snapshot, count + 1);
            snapshot[count++] = pair.Value;
        }

        if (count != snapshot.Length) Array.Resize(ref snapshot, count);
        _routingSnapshot = snapshot;
    }

    internal void DisconnectIdleRepeaters(long nowTicks)
    {
        var cutoffTicks = nowTicks - TimeSpan.FromSeconds(45).Ticks;

        foreach (var kvp in _repeaters)
        {
            var repeater = kvp.Value;
            if (repeater.State != RepeaterState.LoggedIn) continue;

            var lastPing = Volatile.Read(ref repeater.LastPingTicks);
            if (lastPing <= 0 || lastPing >= cutoffTicks) continue;

            var secondsIdle = (nowTicks - lastPing) / TimeSpan.TicksPerSecond;
            logger.LogInformation("Timeout für Repeater {Id} nach {Seconds}s Inaktivität. Setze auf Disconnected", repeater.Id, secondsIdle);

            repeater.State = RepeaterState.Disconnected;
            Volatile.Write(ref repeater.LastPingTicks, 0);
            Volatile.Write(ref repeater.LoggedInSinceTicks, 0);
        }

    }

    // ReSharper disable once CognitiveComplexity
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            DisconnectIdleRepeaters(DateTime.UtcNow.Ticks);
        }
    }
}
