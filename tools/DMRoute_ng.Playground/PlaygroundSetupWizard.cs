using DMRoute_ng.Emulator;

namespace DMRoute_ng.Playground;

internal enum PlaygroundSetupResult
{
    Saved,
    Cancelled,
    Error
}

internal sealed class PlaygroundSetupWizard(IPlaygroundSetupTerminal terminal)
{
    public PlaygroundSetupResult Run(string path)
    {
        if (!terminal.IsInteractive)
        {
            terminal.Write("Der Setup-Wizard benötigt ein interaktives Terminal.\n");
            return PlaygroundSetupResult.Error;
        }

        terminal.Write("DMRoute-ng Emulator-Playground Setup\n");
        terminal.Write($"Zieldatei: {Path.GetFullPath(path)}\n\n");

        var host = Prompt("Master IPv4/DNS", "127.0.0.1");
        if (host is null) return PlaygroundSetupResult.Cancelled;
        var port = PromptInt("Master UDP-Port", 62031, 1, 65535);
        if (port is null) return PlaygroundSetupResult.Cancelled;
        var zonePsk = PromptSecret("Zonen-PSK");
        if (zonePsk is null) return PlaygroundSetupResult.Cancelled;
        var hotspotCount = PromptInt("Anzahl virtueller Hotspots", 2, 1, 64);
        if (hotspotCount is null) return PlaygroundSetupResult.Cancelled;

        var hotspots = new PlaygroundHotspotSettings[hotspotCount.Value];
        for (var index = 0; index < hotspots.Length; index++)
        {
            terminal.Write($"\nHotspot {index + 1}\n");
            var name = Prompt("Name", $"hs{index + 1}");
            if (name is null) return PlaygroundSetupResult.Cancelled;
            var repeaterId = PromptInt("Repeater-ID", 1000001 + index, 1, int.MaxValue);
            if (repeaterId is null) return PlaygroundSetupResult.Cancelled;
            var callsign = Prompt("Callsign", $"PLAY{index + 1}");
            if (callsign is null) return PlaygroundSetupResult.Cancelled;
            var rxFrequency = Prompt("RX-Frequenz", "438800000");
            if (rxFrequency is null) return PlaygroundSetupResult.Cancelled;
            var txFrequency = Prompt("TX-Frequenz", "438800000");
            if (txFrequency is null) return PlaygroundSetupResult.Cancelled;
            var colorCode = PromptInt("Color Code", 1, 0, 99);
            if (colorCode is null) return PlaygroundSetupResult.Cancelled;

            hotspots[index] = new PlaygroundHotspotSettings
            {
                Name = name,
                RepeaterId = repeaterId.Value,
                PreSharedKey = zonePsk + repeaterId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Configuration = new HotspotConfiguration
                {
                    Callsign = callsign,
                    RxFrequency = rxFrequency,
                    TxFrequency = txFrequency,
                    ColorCode = colorCode.Value,
                    Location = "Playground",
                    Description = $"Virtual hotspot {name}"
                }
            };
        }

        var radioCount = PromptInt("Anzahl virtueller Funkgeräte", hotspots.Length, 0, 64);
        if (radioCount is null) return PlaygroundSetupResult.Cancelled;
        var radios = new PlaygroundRadioSettings[radioCount.Value];
        var zoneId = hotspots[0].RepeaterId / 10000;
        for (var index = 0; index < radios.Length; index++)
        {
            var hotspot = hotspots[index % hotspots.Length];
            terminal.Write($"\nFunkgerät {index + 1}\n");
            var name = Prompt("Name", $"radio{index + 1}");
            if (name is null) return PlaygroundSetupResult.Cancelled;
            var defaultRadioId = zoneId is >= 100 and <= 999 ? zoneId * 100 + index + 1 : 10001 + index;
            var radioId = PromptInt("DMR-ID", defaultRadioId, 10000, 99999);
            if (radioId is null) return PlaygroundSetupResult.Cancelled;
            var hotspotName = Prompt("Hotspot", hotspot.Name);
            if (hotspotName is null) return PlaygroundSetupResult.Cancelled;
            var timeslot = PromptInt("Timeslot", 2, 1, 2);
            if (timeslot is null) return PlaygroundSetupResult.Cancelled;
            var talkgroups = PromptTalkgroups("Talkgroups (kommagetrennt)", [9]);
            if (talkgroups is null) return PlaygroundSetupResult.Cancelled;
            radios[index] = new PlaygroundRadioSettings
            {
                Name = name,
                RadioId = radioId.Value,
                Hotspot = hotspotName,
                Timeslot = timeslot.Value,
                Talkgroups = talkgroups
            };
        }

        var settings = new PlaygroundSettings
        {
            Master = new PlaygroundMasterSettings { Host = host, Port = port.Value },
            Hotspots = hotspots,
            Radios = radios
        };

        try
        {
            settings.Validate();
        }
        catch (PlaygroundConfigurationException exception)
        {
            terminal.Write($"Konfigurationsfehler: {exception.Message}\n");
            return PlaygroundSetupResult.Error;
        }

        if (File.Exists(path))
        {
            terminal.Write("Vorhandene Datei überschreiben? [y/N] ");
            var answer = terminal.ReadLine();
            if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(answer, "j", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(answer, "ja", StringComparison.OrdinalIgnoreCase))
            {
                terminal.Write("Setup abgebrochen; die Datei wurde nicht verändert.\n");
                return PlaygroundSetupResult.Cancelled;
            }
        }

        PlaygroundConfigurationFile.SaveAtomic(path, settings);
        terminal.Write($"Konfiguration gespeichert: {Path.GetFullPath(path)}\n");
        return PlaygroundSetupResult.Saved;
    }

    private string? Prompt(string label, string defaultValue)
    {
        terminal.Write($"{label} [{defaultValue}]: ");
        var input = terminal.ReadLine();
        if (input is null) return null;
        return string.IsNullOrWhiteSpace(input) ? defaultValue : input.Trim();
    }

    private int? PromptInt(string label, int defaultValue, int minimum, int maximum)
    {
        while (true)
        {
            var input = Prompt(label, defaultValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (input is null) return null;
            if (int.TryParse(input, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var value) &&
                value >= minimum && value <= maximum)
                return value;
            terminal.Write($"Bitte eine ganze Zahl zwischen {minimum} und {maximum} eingeben.\n");
        }
    }

    private string? PromptSecret(string label)
    {
        while (true)
        {
            var value = terminal.ReadSecret($"{label} (Esc bricht ab): ");
            if (value is null) return null;
            if (value.Length > 0) return value;
            terminal.Write("Das PSK darf nicht leer sein.\n");
        }
    }

    private int[]? PromptTalkgroups(string label, int[] defaultValue)
    {
        var defaultText = string.Join(',', defaultValue);
        while (true)
        {
            var input = Prompt(label, defaultText);
            if (input is null) return null;
            var parts = input.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var values = new int[parts.Length];
            var valid = parts.Length > 0;
            for (var index = 0; index < parts.Length && valid; index++)
                valid = int.TryParse(parts[index], out values[index]) && values[index] is > 0 and <= 0xFFFFFF;
            if (valid && values.Distinct().Count() == values.Length) return values;
            terminal.Write("Bitte eindeutige Talkgroup-IDs zwischen 1 und 16777215 eingeben.\n");
        }
    }
}
