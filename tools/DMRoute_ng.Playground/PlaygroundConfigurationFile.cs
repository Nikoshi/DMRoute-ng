using System.Text.Json;

namespace DMRoute_ng.Playground;

internal static class PlaygroundConfigurationFile
{
    public const string DefaultPath = "dmroute-playground.json";

    public static PlaygroundSettings Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
            throw new PlaygroundConfigurationException($"Die Playground-Konfiguration '{path}' wurde nicht gefunden.");

        try
        {
            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize(json, PlaygroundJsonContext.Default.PlaygroundSettings)
                           ?? throw new PlaygroundConfigurationException("Die Playground-Konfiguration ist leer.");
            settings.Validate();
            return settings;
        }
        catch (JsonException exception)
        {
            throw new PlaygroundConfigurationException(
                $"Die Playground-Konfiguration ist kein gültiges JSON: {exception.Message}", exception);
        }
    }

    public static void SaveAtomic(string path, PlaygroundSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            var json = JsonSerializer.Serialize(settings, PlaygroundJsonContext.Default.PlaygroundSettings);
            File.WriteAllText(temporaryPath, json + Environment.NewLine);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporaryPath, fullPath, overwrite: true);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(fullPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
