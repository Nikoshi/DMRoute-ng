namespace DMRoute_ng.Playground;

internal sealed record PlaygroundStartupArguments(
    bool Setup, bool Help, string ConfigPath, string? OpenDmrExecutable,
    string? VoiceEncoderExecutable, string[] Command)
{
    public static bool TryParse(string[] args, out PlaygroundStartupArguments parsed, out string? error)
    {
        var setup = false;
        var help = false;
        var configPath = PlaygroundConfigurationFile.DefaultPath;
        var configSeen = false;
        string? openDmrExecutable = null;
        string? voiceEncoderExecutable = null;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument is "--help" or "-h")
            {
                help = true;
                continue;
            }

            if (argument == "--setup")
            {
                if (setup)
                {
                    parsed = Empty;
                    error = "--setup darf nur einmal angegeben werden.";
                    return false;
                }
                setup = true;
                continue;
            }

            if (argument == "--config")
            {
                if (configSeen || ++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                {
                    parsed = Empty;
                    error = "--config benötigt genau einen Dateipfad.";
                    return false;
                }
                configSeen = true;
                configPath = args[index];
                continue;
            }

            if (argument == "--open-dmr")
            {
                if (openDmrExecutable is not null || ++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                {
                    parsed = Empty;
                    error = "--open-dmr benötigt genau einen Dateipfad.";
                    return false;
                }
                openDmrExecutable = args[index];
                continue;
            }

            if (argument == "--voice-encoder")
            {
                if (voiceEncoderExecutable is not null || ++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                {
                    parsed = Empty;
                    error = "--voice-encoder benötigt genau einen Dateipfad.";
                    return false;
                }
                voiceEncoderExecutable = args[index];
                continue;
            }

            if (argument.Equals("voice", StringComparison.OrdinalIgnoreCase))
            {
                parsed = new PlaygroundStartupArguments(
                    setup, help, configPath, openDmrExecutable, voiceEncoderExecutable, args[index..]);
                error = null;
                return true;
            }

            parsed = Empty;
            error = $"Unbekanntes Argument: {argument}";
            return false;
        }

        parsed = new PlaygroundStartupArguments(setup, help, configPath, openDmrExecutable, voiceEncoderExecutable, []);
        error = null;
        return true;
    }

    private static PlaygroundStartupArguments Empty { get; } =
        new(false, false, PlaygroundConfigurationFile.DefaultPath, null, null, []);
}
