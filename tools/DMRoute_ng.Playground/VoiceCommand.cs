using System.Globalization;
using DMRoute_ng.Audio;
using DMRoute_ng.Coding;

namespace DMRoute_ng.Playground;

internal static class VoiceCommand
{
    public static async Task<int> RunAsync(PlaygroundStartupArguments startup, CancellationToken cancellationToken)
    {
        var arguments = startup.Command;
        if (arguments.Length < 2) throw Usage();
        PlaygroundSettings? settings = null;
        var isEncode = arguments[1].Equals("encode", StringComparison.OrdinalIgnoreCase);
        if (isEncode || (startup.OpenDmrExecutable is null && File.Exists(startup.ConfigPath)))
            settings = PlaygroundConfigurationFile.Load(startup.ConfigPath);
        var executable = ResolveExecutable(startup.OpenDmrExecutable, settings?.Codec?.OpenDmrExecutable);
        var codec = new OpenDmrProcessCodec(executable);

        switch (arguments[1].ToLowerInvariant())
        {
            case "info" when arguments.Length == 2:
                Console.WriteLine(await codec.GetInfoAsync(cancellationToken).ConfigureAwait(false));
                return 0;
            case "decode" when arguments.Length == 4:
            {
                var ambe = Path.GetExtension(arguments[2]).Equals(".ambe", StringComparison.OrdinalIgnoreCase)
                    ? await File.ReadAllBytesAsync(arguments[2], cancellationToken).ConfigureAwait(false)
                    : VoiceScenarioFile.ReadAmbe(arguments[2]);
                if (ambe.Length == 0 || ambe.Length % OpenDmrProcessCodec.AmbeBytesPerFrame != 0)
                    throw new InvalidDataException("Die AMBE-Eingabe muss vollständige Frames mit je 9 Byte enthalten.");
                var samples = await codec.DecodeAsync(ambe, cancellationToken).ConfigureAwait(false);
                await WavePcm16.WriteAsync(arguments[3], samples, cancellationToken).ConfigureAwait(false);
                Console.WriteLine($"{samples.Length} Samples nach '{arguments[3]}' geschrieben.");
                return 0;
            }
            case "encode" when arguments.Length == 7:
            {
                settings ??= PlaygroundConfigurationFile.Load(startup.ConfigPath);
                var radio = settings.Radios.FirstOrDefault(r => r.Name.Equals(arguments[2], StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"Unbekanntes Funkgerät '{arguments[2]}'.");
                var hotspot = settings.Hotspots.First(h => h.Name.Equals(radio.Hotspot, StringComparison.OrdinalIgnoreCase));
                var isGroupCall = arguments[3].Equals("tg", StringComparison.OrdinalIgnoreCase);
                if (!isGroupCall && !arguments[3].Equals("fug", StringComparison.OrdinalIgnoreCase)) throw Usage();
                if (!int.TryParse(arguments[4], NumberStyles.None, CultureInfo.InvariantCulture, out var destinationId) ||
                    destinationId is <= 0 or > 0xFFFFFF)
                    throw new ArgumentException("ziel-id muss zwischen 1 und 16777215 liegen.");
                var samples = await WavePcm16.ReadAsync(arguments[5], cancellationToken).ConfigureAwait(false);
                if (samples.Length == 0) throw new InvalidDataException("Die WAV-Datei enthält keine Samples.");
                var paddedLength = checked((samples.Length + 479) / 480 * 480);
                if (paddedLength / 480 + 5 > 10_000)
                    throw new InvalidDataException("Die WAV-Datei ist für ein Szenario mit höchstens 10000 Schritten zu lang.");
                if (paddedLength != samples.Length) Array.Resize(ref samples, paddedLength);
                var ambe = await codec.EncodeAsync(samples, cancellationToken).ConfigureAwait(false);
                await VoiceScenarioFile.WriteAsync(arguments[6], radio, hotspot.RepeaterId, destinationId,
                    isGroupCall, ambe, cancellationToken).ConfigureAwait(false);
                Console.WriteLine($"Sprachszenario mit {ambe.Length / 9} AMBE-Frames nach '{arguments[6]}' geschrieben.");
                return 0;
            }
            default:
                throw Usage();
        }
    }

    private static string ResolveExecutable(string? commandLine, string? configured)
    {
        if (!string.IsNullOrWhiteSpace(commandLine)) return commandLine;
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        var fileName = OperatingSystem.IsWindows() ? "dmr_codec.exe" : "dmr_codec";
        var sibling = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(sibling)) return sibling;
        return fileName;
    }

    private static ArgumentException Usage() => new(
        "Verwendung: voice info | voice decode <eingabe.dmrd|eingabe.ambe> <ausgabe.wav> | " +
        "voice encode <radio> <tg|fug> <ziel-id> <eingabe.wav> <ausgabe.dmrd>");
}
