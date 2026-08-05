using DMRoute_ng.Registry;
using DMRoute_ng.Types;

namespace DMRoute_ng.Tests;

public class RepeaterRegistryTests
{
    [Fact]
    public void AddAndRetrieveRepeater_ShouldSucceed()
    {
        // Arrange
        var registry = new RepeaterRegistry();
        var repeater = new Repeater(262101, "secret123", RepeaterState.Disconnected, null);

        // Act
        registry.AddOrUpdate(repeater);
        var found = registry.TryGet(262101, out var retrieved);

        // Assert
        Assert.True(found);
        Assert.NotNull(retrieved);
        Assert.Equal("secret123", retrieved.PreSharedKey);
        Assert.Equal(RepeaterState.Disconnected, retrieved.State);
    }

    [Fact]
    public void UpdateRepeaterState_ShouldModifyExisting()
    {
        // Arrange
        var registry = new RepeaterRegistry();
        var repeater = new Repeater(262102, "secret123", RepeaterState.Disconnected, null);
        registry.AddOrUpdate(repeater);

        // Act
        repeater.State = RepeaterState.LoggedIn;
        registry.AddOrUpdate(repeater);
        registry.TryGet(262102, out var retrieved);

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal(RepeaterState.LoggedIn, retrieved.State);
    }
}