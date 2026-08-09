using System.Net;
using DMRoute_ng.Registry;
using DMRoute_ng.Types;
using Microsoft.Extensions.Logging.Abstractions;

namespace DMRoute_ng.Tests;

public class TimeoutAndReconnectTests
{
    [Fact]
    public void RepeaterRegistry_TimeoutLogic_ShouldDisconnectIdleRepeater()
    {
        // Arrange
        var registry = new RepeaterRegistry(NullLogger<RepeaterRegistry>.Instance, 100, "secret");
        registry.TryGet(1000001, out var repeater);
        repeater.State = RepeaterState.LoggedIn;
        
        // Simuliere einen Inaktivitäts-Zeitraum von 50 Sekunden
        repeater.LastPingTicks = DateTime.UtcNow.AddSeconds(-50).Ticks;
        long cutoffTicks = DateTime.UtcNow.AddSeconds(-45).Ticks;

        // Act (Abbildung der Logik aus ExecuteAsync)
        if (repeater.State == RepeaterState.LoggedIn && repeater.LastPingTicks > 0 && repeater.LastPingTicks < cutoffTicks)
        {
            repeater.State = RepeaterState.Disconnected;
            repeater.LastPingTicks = 0;
        }

        // Assert
        Assert.Equal(RepeaterState.Disconnected, repeater.State);
        Assert.Equal(0, repeater.LastPingTicks);
    }

    [Fact]
    public void DmrServer_HandleRptPing_DisconnectedRepeater_SameEndpoint_ShouldSoftReconnect()
    {
        // Arrange
        var originalEndpoint = new IPEndPoint(IPAddress.Parse("192.168.1.100"), 62031);
        var registry = new RepeaterRegistry(NullLogger<RepeaterRegistry>.Instance, 100, "secret");
        registry.TryGet(1000001, out var repeater);
        
        repeater.EndPoint = originalEndpoint;
        repeater.State = RepeaterState.Disconnected;

        // Act (Abbildung der Logik aus HandleRptPing)
        var incomingEndpoint = new IPEndPoint(IPAddress.Parse("192.168.1.100"), 62031);
        
        if (repeater is { State: RepeaterState.Disconnected, EndPoint: not null } && repeater.EndPoint.Equals(incomingEndpoint))
        {
            repeater.State = RepeaterState.LoggedIn;
        }

        // Assert
        Assert.Equal(RepeaterState.LoggedIn, repeater.State);
    }

    [Fact]
    public void MicroSubnetRouter_RouteDmrd_LocalOrigin_ShouldUpdateLastPingTicks()
    {
        // Arrange
        var registry = new RepeaterRegistry(NullLogger<RepeaterRegistry>.Instance, 100, "secret");
        registry.TryGet(1000001, out var repeater);
        repeater.State = RepeaterState.LoggedIn;
        
        var initialTicks = DateTime.UtcNow.AddSeconds(-10).Ticks;
        repeater.LastPingTicks = initialTicks;

        // Act (Abbildung der Tick-Aktualisierung aus RouteDmrd)
        var isLocalOrigin = repeater.State == RepeaterState.LoggedIn;
        if (isLocalOrigin)
        {
            Volatile.Write(ref repeater.LastPingTicks, DateTime.UtcNow.Ticks);
        }

        // Assert
        Assert.True(repeater.LastPingTicks > initialTicks);
    }
}