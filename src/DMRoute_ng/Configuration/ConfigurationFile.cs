using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace DMRoute_ng.Configuration;

internal static class ConfigurationFile
{
    public const string DefaultPath = "dmroute.json";

    public static DmRouteSettings LoadOrDefault(string path)
    {
        if (!File.Exists(path)) return new DmRouteSettings();

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.GetFullPath(path), optional: false, reloadOnChange: false)
            .Build();
        return DmRouteSettings.ReadConfiguration(configuration);
    }

    public static void SaveAtomic(string path, DmRouteSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        settings.Validate();

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            var json = JsonSerializer.Serialize(settings, DmRouteJsonContext.Default.DmRouteSettings);
            File.WriteAllText(temporaryPath, json + Environment.NewLine);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporaryPath, fullPath, overwrite: true);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(fullPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
