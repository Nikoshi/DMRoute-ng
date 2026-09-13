using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

namespace DMRoute_ng.Configuration;

public sealed record DmRouteSettings
{
    public const string DefaultMeshPsk = "s3cr37m3sh";
    public const string DefaultZonePsk = "s3cr37w0rd";

    public int ZoneId { get; init; } = 100;
    public string ZonePsk { get; init; } = DefaultZonePsk;
    public string MeshPsk { get; init; } = DefaultMeshPsk;
    public RoutingSettings Routing { get; init; } = new();
    public SdsSettings Sds { get; init; } = new();
    public MqttSettings Mqtt { get; init; } = new();

    public static DmRouteSettings FromConfiguration(IConfiguration configuration)
    {
        var settings = ReadConfiguration(configuration);
        settings.Validate();
        return settings;
    }

    internal static DmRouteSettings ReadConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new DmRouteSettings
        {
            ZoneId = ReadInt(configuration, "ZoneId", 100),
            ZonePsk = ReadString(configuration, "ZonePsk", DefaultZonePsk),
            MeshPsk = ReadString(configuration, "MeshPsk", DefaultMeshPsk),
            Routing = new RoutingSettings
            {
                MaxActiveCalls = ReadInt(configuration, "Routing:MaxActiveCalls", 256),
                MaxLocalDeviceRoutes = ReadInt(configuration, "Routing:MaxLocalDeviceRoutes", 8192)
            },
            Sds = new SdsSettings
            {
                MaxSessions = ReadInt(configuration, "Sds:MaxSessions", 128),
                MaxMessageBytes = ReadInt(configuration, "Sds:MaxMessageBytes", 4096)
            },
            Mqtt = new MqttSettings
            {
                Host = ReadString(configuration, "Mqtt:Host", string.Empty),
                Port = ReadInt(configuration, "Mqtt:Port", 1883),
                LocationPrivacyEnabled = ReadBool(configuration, "Mqtt:LocationPrivacyEnabled", true),
                LocationGridKm = ReadDouble(configuration, "Mqtt:LocationGridKm", 10d),
                StateIntervalSeconds = ReadInt(configuration, "Mqtt:StateIntervalSeconds", 10),
                EventCapacity = ReadInt(configuration, "Mqtt:EventCapacity", 1000),
                DiagnosticFrameCapacity = ReadInt(configuration, "Mqtt:DiagnosticFrameCapacity", 128),
                MaxDiagnosticFrameBytes = ReadInt(configuration, "Mqtt:MaxDiagnosticFrameBytes", 1024),
                PayloadBufferBytes = ReadInt(configuration, "Mqtt:PayloadBufferBytes", 32768)
            }
        };

    }

    public void Validate()
    {
        RequirePositive(ZoneId, "ZoneId");
        RequireText(ZonePsk, "ZonePsk");
        RequireText(MeshPsk, "MeshPsk");
        RequirePositive(Routing.MaxActiveCalls, "Routing:MaxActiveCalls");
        RequirePositive(Routing.MaxLocalDeviceRoutes, "Routing:MaxLocalDeviceRoutes");
        RequirePositive(Sds.MaxSessions, "Sds:MaxSessions");
        RequirePositive(Sds.MaxMessageBytes, "Sds:MaxMessageBytes");
        RequireText(Mqtt.Host, "Mqtt:Host");
        if (Mqtt.Port is <= 0 or > 65535)
            throw Invalid("Mqtt:Port", "must be between 1 and 65535");
        if (!double.IsFinite(Mqtt.LocationGridKm) || Mqtt.LocationGridKm is <= 0d or > 1000d)
            throw Invalid("Mqtt:LocationGridKm", "must be finite, positive, and at most 1000 km");
        RequirePositive(Mqtt.StateIntervalSeconds, "Mqtt:StateIntervalSeconds");
        RequirePositive(Mqtt.EventCapacity, "Mqtt:EventCapacity");
        RequirePositive(Mqtt.DiagnosticFrameCapacity, "Mqtt:DiagnosticFrameCapacity");
        RequirePositive(Mqtt.MaxDiagnosticFrameBytes, "Mqtt:MaxDiagnosticFrameBytes");
        if (Mqtt.PayloadBufferBytes < 32768)
            throw Invalid("Mqtt:PayloadBufferBytes", "must be at least 32768");
    }

    private static string ReadString(IConfiguration configuration, string key, string defaultValue)
    {
        var value = configuration[key];
        return value is null ? defaultValue : value;
    }

    private static int ReadInt(IConfiguration configuration, string key, int defaultValue)
    {
        var value = configuration[key];
        if (value is null) return defaultValue;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        throw Invalid(key, $"'{value}' is not a valid integer");
    }

    private static double ReadDouble(IConfiguration configuration, string key, double defaultValue)
    {
        var value = configuration[key];
        if (value is null) return defaultValue;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        throw Invalid(key, $"'{value}' is not a valid number");
    }

    private static bool ReadBool(IConfiguration configuration, string key, bool defaultValue)
    {
        var value = configuration[key];
        if (value is null) return defaultValue;
        if (bool.TryParse(value, out var parsed)) return parsed;
        if (value == "1") return true;
        if (value == "0") return false;
        throw Invalid(key, $"'{value}' is not a valid boolean");
    }

    private static void RequirePositive(int value, string key)
    {
        if (value <= 0) throw Invalid(key, "must be positive");
    }

    private static void RequireText(string value, string key)
    {
        if (string.IsNullOrWhiteSpace(value)) throw Invalid(key, "must not be empty");
    }

    private static ConfigurationValidationException Invalid(string key, string message) =>
        new(key, $"Configuration value '{key}' {message}.");
}

public sealed record RoutingSettings
{
    public int MaxActiveCalls { get; init; } = 256;
    public int MaxLocalDeviceRoutes { get; init; } = 8192;
}

public sealed record SdsSettings
{
    public int MaxSessions { get; init; } = 128;
    public int MaxMessageBytes { get; init; } = 4096;
}

public sealed record MqttSettings
{
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; } = 1883;
    public bool LocationPrivacyEnabled { get; init; } = true;
    public double LocationGridKm { get; init; } = 10d;
    public int StateIntervalSeconds { get; init; } = 10;
    public int EventCapacity { get; init; } = 1000;
    public int DiagnosticFrameCapacity { get; init; } = 128;
    public int MaxDiagnosticFrameBytes { get; init; } = 1024;
    public int PayloadBufferBytes { get; init; } = 32768;
}

public sealed class ConfigurationValidationException(string key, string message) : Exception(message)
{
    public string Key { get; } = key;
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(DmRouteSettings))]
internal sealed partial class DmRouteJsonContext : JsonSerializerContext;
