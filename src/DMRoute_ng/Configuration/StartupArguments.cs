namespace DMRoute_ng.Configuration;

internal sealed record StartupArguments(bool Setup, string? ConfigPath, string[] ConfigurationArguments)
{
    public static bool TryParse(string[] args, out StartupArguments parsed, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);

        var setup = false;
        string? configPath = null;
        var configurationArguments = new List<string>(args.Length);

        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            if (argument.Equals("--setup", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("-Setup", StringComparison.OrdinalIgnoreCase))
            {
                if (setup)
                {
                    parsed = Empty;
                    error = "The setup option may only be specified once.";
                    return false;
                }

                setup = true;
                continue;
            }

            if (argument.Equals("--config", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("-Config", StringComparison.OrdinalIgnoreCase))
            {
                if (configPath is not null)
                {
                    parsed = Empty;
                    error = "The config option may only be specified once.";
                    return false;
                }

                if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]))
                {
                    parsed = Empty;
                    error = $"The {argument} option requires a file path.";
                    return false;
                }

                configPath = args[i];
                continue;
            }

            configurationArguments.Add(argument);
        }

        parsed = new StartupArguments(setup, configPath, configurationArguments.ToArray());
        error = null;
        return true;
    }

    private static StartupArguments Empty { get; } = new(false, null, []);
}
