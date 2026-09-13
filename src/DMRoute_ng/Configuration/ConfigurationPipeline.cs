using Microsoft.Extensions.Configuration;

namespace DMRoute_ng.Configuration;

internal static class ConfigurationPipeline
{
    public static void ApplyOverrides(ConfigurationManager configuration, StartupArguments startup)
    {
        if (startup.ConfigPath is not null)
            configuration.AddJsonFile(Path.GetFullPath(startup.ConfigPath), optional: false, reloadOnChange: false);

        // The default host has already loaded appsettings.json and environment variables.
        // Re-add the environment provider here so it wins over the selected JSON file.
        configuration.AddEnvironmentVariables();
        configuration.AddCommandLine(startup.ConfigurationArguments);
    }
}
