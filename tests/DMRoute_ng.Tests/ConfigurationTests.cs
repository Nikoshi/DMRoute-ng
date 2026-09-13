using System.Text;
using System.Text.Json;
using DMRoute_ng.Configuration;
using Microsoft.Extensions.Configuration;

namespace DMRoute_ng.Tests;

[Collection(nameof(ConfigurationEnvironmentCollection))]
public sealed class ConfigurationTests
{
    [Fact]
    public void StartupArguments_RemoveOnlyControlOptions()
    {
        var success = StartupArguments.TryParse(
            ["--setup", "-Config", "custom.json", "--Mqtt:Host", "broker", "--ZoneId=23"],
            out var parsed,
            out var error);

        Assert.True(success, error);
        Assert.True(parsed.Setup);
        Assert.Equal("custom.json", parsed.ConfigPath);
        Assert.Equal(["--Mqtt:Host", "broker", "--ZoneId=23"], parsed.ConfigurationArguments);
    }

    [Theory]
    [InlineData("--setup", "-Setup")]
    [InlineData("--config", "one.json", "-Config", "two.json")]
    public void StartupArguments_RejectDuplicateControlOptions(params string[] args)
    {
        Assert.False(StartupArguments.TryParse(args, out _, out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("--config")]
    [InlineData("-Config", "")]
    public void StartupArguments_RequireConfigPath(params string[] args)
    {
        Assert.False(StartupArguments.TryParse(args, out _, out var error));
        Assert.Contains("requires a file path", error);
    }

    [Fact]
    public void ConfigurationPipeline_AppliesCliAfterEnvironmentAfterSelectedJson()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "selected.json");
        File.WriteAllText(path, """
            {
              "ZoneId": 101,
              "ZonePsk": "file-zone",
              "MeshPsk": "file-mesh",
              "Mqtt": { "Host": "file-broker", "Port": 1881 }
            }
            """);
        var oldZoneId = Environment.GetEnvironmentVariable("ZoneId");
        var oldMqttPort = Environment.GetEnvironmentVariable("Mqtt__Port");

        try
        {
            Environment.SetEnvironmentVariable("ZoneId", "102");
            Environment.SetEnvironmentVariable("Mqtt__Port", "1882");
            Assert.True(StartupArguments.TryParse(
                ["--config", path, "--ZoneId=103", "--Mqtt:Host", "cli-broker"],
                out var startup,
                out _));
            var configuration = new ConfigurationManager();
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ZoneId"] = "100",
                ["Mqtt:Host"] = "appsettings-broker"
            });

            ConfigurationPipeline.ApplyOverrides(configuration, startup);
            var settings = DmRouteSettings.FromConfiguration(configuration);

            Assert.Equal(103, settings.ZoneId);
            Assert.Equal("cli-broker", settings.Mqtt.Host);
            Assert.Equal(1882, settings.Mqtt.Port);
            Assert.Equal("file-zone", settings.ZonePsk);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZoneId", oldZoneId);
            Environment.SetEnvironmentVariable("Mqtt__Port", oldMqttPort);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Settings_RejectInvalidScalarAndRange()
    {
        var malformed = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mqtt:Host"] = "broker",
            ["Routing:MaxActiveCalls"] = "many"
        }).Build();
        var invalidRange = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mqtt:Host"] = "broker",
            ["Mqtt:Port"] = "65536"
        }).Build();

        Assert.Equal("Routing:MaxActiveCalls",
            Assert.Throws<ConfigurationValidationException>(() => DmRouteSettings.FromConfiguration(malformed)).Key);
        Assert.Equal("Mqtt:Port",
            Assert.Throws<ConfigurationValidationException>(() => DmRouteSettings.FromConfiguration(invalidRange)).Key);
    }

    [Fact]
    public void GeneratedJson_RoundTripsWithoutReflection()
    {
        var original = ValidSettings() with { ZoneId = 314 };
        var json = JsonSerializer.Serialize(original, DmRouteJsonContext.Default.DmRouteSettings);
        var copy = JsonSerializer.Deserialize(json, DmRouteJsonContext.Default.DmRouteSettings);

        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        Assert.Equal(original, copy);
        Assert.Contains("\"Routing\"", json);
        Assert.Contains("\"Sds\"", json);
        Assert.Contains("\"Mqtt\"", json);
    }

    internal static DmRouteSettings ValidSettings() => new()
    {
        ZonePsk = "zone-secret",
        MeshPsk = "mesh-secret",
        Mqtt = new MqttSettings { Host = "127.0.0.1" }
    };

    internal static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dmroute-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}

[CollectionDefinition(nameof(ConfigurationEnvironmentCollection), DisableParallelization = true)]
public sealed class ConfigurationEnvironmentCollection;
