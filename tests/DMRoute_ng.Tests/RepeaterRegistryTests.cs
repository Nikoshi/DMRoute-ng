using DMRoute_ng.Registry;
using DMRoute_ng.Types;
using Microsoft.Extensions.Logging.Abstractions;

namespace DMRoute_ng.Tests;

public class RepeaterRegistryTests
{
    [Fact]
    public void TryGet_WithValidZoneId_ShouldCreateAndRetrieveRepeater()
    {
        // Arrange
        var logger = NullLogger<RepeaterRegistry>.Instance;
        // MasterZoneId 26 bedeutet: Repeater-IDs müssen mit 26... beginnen (z. B. 1000001)
        var registry = new RepeaterRegistry(logger, masterZoneId: 100, sharedPsk: "secret");

        // Act
        // 1000001 / 10000 = 100 (Mathematik stimmt überein)
        var found = registry.TryGet(1000001, out var retrieved);

        // Assert
        Assert.True(found);
        Assert.NotNull(retrieved);
        // PSK muss aus Master-PSK und Repeater-ID zusammengesetzt sein
        Assert.Equal("secret1000001", retrieved.PreSharedKey);
        Assert.Equal(RepeaterState.Disconnected, retrieved.State);
    }

    [Fact]
    public void TryGet_WithInvalidZoneId_ShouldReturnFalse()
    {
        // Arrange
        var logger = NullLogger<RepeaterRegistry>.Instance;
        var registry = new RepeaterRegistry(logger, masterZoneId: 26, sharedPsk: "secret");

        // Act
        // 992101 / 10000 = 99 (Passt nicht zur MasterZoneId 26)
        var found = registry.TryGet(992101, out var retrieved);

        // Assert
        Assert.False(found);
        Assert.Null(retrieved);
    }

    [Fact]
    public void TryGet_MultipleTimes_ShouldReturnSameInstance()
    {
        // Arrange
        var logger = NullLogger<RepeaterRegistry>.Instance;
        var registry = new RepeaterRegistry(logger, masterZoneId: 26, sharedPsk: "secret");

        // Act
        registry.TryGet(262101, out var first);
        first.State = RepeaterState.LoggedIn;
        
        // Erneuter Abruf derselben ID
        registry.TryGet(262101, out var second);

        // Assert
        Assert.Same(first, second);
        Assert.Equal(RepeaterState.LoggedIn, second.State);
    }
}