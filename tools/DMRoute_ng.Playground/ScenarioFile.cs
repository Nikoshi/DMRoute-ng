using System.Buffers.Binary;
using System.Globalization;
using DMRoute_ng.Emulator;

namespace DMRoute_ng.Playground;

internal readonly record struct ScenarioLoadResult(RadioScenario Scenario, int RewrittenRepeaterIds);

internal static class ScenarioFile
{
    private const int MaxSteps = 10_000;
    private const int MaxPacketBytes = 4096;

    public static ScenarioLoadResult Load(string path, int targetRepeaterId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetRepeaterId);
        if (!File.Exists(path)) throw new ScenarioFormatException($"Szenariodatei nicht gefunden: {path}");

        string? name = null;
        int? radioId = null;
        var rewritten = 0;
        var steps = new List<RadioScenarioStep>();
        var lines = File.ReadLines(path);
        var lineNumber = 0;

        foreach (var rawLine in lines)
        {
            lineNumber++;
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            if (line.StartsWith("name=", StringComparison.OrdinalIgnoreCase))
            {
                if (name is not null) throw Error(lineNumber, "name darf nur einmal vorkommen.");
                name = line[5..].Trim();
                if (name.Length == 0) throw Error(lineNumber, "name darf nicht leer sein.");
                continue;
            }

            if (line.StartsWith("radioId=", StringComparison.OrdinalIgnoreCase))
            {
                if (radioId is not null) throw Error(lineNumber, "radioId darf nur einmal vorkommen.");
                if (!int.TryParse(line[8..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
                    parsed <= 0)
                    throw Error(lineNumber, "radioId muss eine positive ganze Zahl sein.");
                radioId = parsed;
                continue;
            }

            if (steps.Count == MaxSteps)
                throw Error(lineNumber, $"Ein Szenario darf höchstens {MaxSteps} Schritte enthalten.");

            var separator = FindWhitespace(line);
            if (separator <= 0)
                throw Error(lineNumber, "Erwartet wird '<delay-ms> <DMRD-hex>'.");
            var delayText = line[..separator];
            var hex = line[separator..].Trim();
            if (!int.TryParse(delayText, NumberStyles.None, CultureInfo.InvariantCulture, out var delayMs) || delayMs < 0)
                throw Error(lineNumber, "delay-ms muss zwischen 0 und 2147483647 liegen.");
            if (hex.Length == 0 || (hex.Length & 1) != 0)
                throw Error(lineNumber, "DMRD-hex muss eine gerade Zahl von Hex-Zeichen enthalten.");
            if (hex.Length / 2 > MaxPacketBytes)
                throw Error(lineNumber, $"Ein Frame darf höchstens {MaxPacketBytes} Bytes enthalten.");

            byte[] packet;
            try
            {
                packet = Convert.FromHexString(hex);
            }
            catch (FormatException exception)
            {
                throw Error(lineNumber, "DMRD-hex enthält ungültige Zeichen.", exception);
            }

            if (!HomebrewProtocol.TryReadDmrdInfo(packet, out var info))
                throw Error(lineNumber, "Der Schritt enthält kein vollständiges DMRD-Datagramm.");
            if (info.RepeaterId != targetRepeaterId)
            {
                BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(11, 4), targetRepeaterId);
                rewritten++;
            }

            steps.Add(new RadioScenarioStep(packet, TimeSpan.FromMilliseconds(delayMs)));
        }

        if (name is null) throw new ScenarioFormatException("Das Szenario benötigt eine name-Direktive.");
        if (radioId is null) throw new ScenarioFormatException("Das Szenario benötigt eine radioId-Direktive.");

        try
        {
            return new ScenarioLoadResult(new RadioScenario(name, radioId.Value, steps), rewritten);
        }
        catch (ArgumentException exception)
        {
            throw new ScenarioFormatException(exception.Message, exception);
        }
    }

    private static int FindWhitespace(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsWhiteSpace(value[index])) return index;
        }
        return -1;
    }

    private static ScenarioFormatException Error(int line, string message, Exception? inner = null) =>
        new($"Zeile {line}: {message}", inner);
}

internal sealed class ScenarioFormatException(string message, Exception? innerException = null)
    : Exception(message, innerException);
