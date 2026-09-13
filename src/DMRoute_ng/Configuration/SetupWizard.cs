using System.Globalization;

namespace DMRoute_ng.Configuration;

internal enum SetupResult
{
    Saved,
    Cancelled,
    Error
}

internal sealed class SetupWizard(ISetupTerminal terminal)
{
    private enum Field
    {
        ZoneId,
        ZonePsk,
        MeshPsk,
        MqttHost,
        MqttPort,
        LocationPrivacyEnabled,
        LocationGridKm,
        MaxActiveCalls,
        MaxLocalDeviceRoutes,
        MaxSdsSessions,
        MaxSdsMessageBytes,
        StateIntervalSeconds,
        EventCapacity,
        DiagnosticFrameCapacity,
        MaxDiagnosticFrameBytes,
        PayloadBufferBytes
    }

    private static readonly Field[] BasicFields =
    [
        Field.ZoneId,
        Field.ZonePsk,
        Field.MeshPsk,
        Field.MqttHost,
        Field.MqttPort,
        Field.LocationPrivacyEnabled,
        Field.LocationGridKm
    ];

    private static readonly Field[] AllFields =
    [
        .. BasicFields,
        Field.MaxActiveCalls,
        Field.MaxLocalDeviceRoutes,
        Field.MaxSdsSessions,
        Field.MaxSdsMessageBytes,
        Field.StateIntervalSeconds,
        Field.EventCapacity,
        Field.DiagnosticFrameCapacity,
        Field.MaxDiagnosticFrameBytes,
        Field.PayloadBufferBytes
    ];

    public SetupResult Run(string path, DmRouteSettings initialSettings)
    {
        if (!terminal.IsInteractive)
        {
            terminal.Write("Setup requires an interactive terminal.\n");
            return SetupResult.Error;
        }

        var settings = initialSettings;
        var advanced = false;
        var selected = 0;
        var status = string.Empty;

        while (true)
        {
            var fields = advanced ? AllFields : BasicFields;
            if (selected >= fields.Length) selected = fields.Length - 1;
            Render(path, settings, fields, selected, advanced, status);
            status = string.Empty;

            var key = terminal.ReadKey();
            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    selected = selected == 0 ? fields.Length - 1 : selected - 1;
                    break;
                case ConsoleKey.DownArrow:
                    selected = (selected + 1) % fields.Length;
                    break;
                case ConsoleKey.A:
                    advanced = !advanced;
                    break;
                case ConsoleKey.Escape:
                    return SetupResult.Cancelled;
                case ConsoleKey.F10:
                    if (TrySave(path, settings, out status)) return SetupResult.Saved;
                    break;
                case ConsoleKey.S:
                    if (TrySave(path, settings, out status)) return SetupResult.Saved;
                    break;
                case ConsoleKey.Spacebar when fields[selected] == Field.LocationPrivacyEnabled:
                    settings = settings with
                    {
                        Mqtt = settings.Mqtt with
                        {
                            LocationPrivacyEnabled = !settings.Mqtt.LocationPrivacyEnabled
                        }
                    };
                    break;
                case ConsoleKey.Enter:
                    settings = Edit(settings, fields[selected], out status);
                    break;
            }
        }
    }

    private void Render(
        string path,
        DmRouteSettings settings,
        Field[] fields,
        int selected,
        bool advanced,
        string status)
    {
        terminal.Clear();
        terminal.Write("DMRoute-ng configuration\n");
        terminal.Write($"Target: {Path.GetFullPath(path)}\n\n");
        terminal.Write("  Basic settings\n");
        for (var i = 0; i < fields.Length; i++)
        {
            if (advanced && i == BasicFields.Length) terminal.Write("\n  Advanced settings\n");
            terminal.Write(i == selected ? "> " : "  ");
            terminal.Write($"{Label(fields[i]),-29} {DisplayValue(settings, fields[i])}\n");
        }

        terminal.Write($"\nAdvanced settings: {(advanced ? "shown" : "hidden")} (A)\n");
        terminal.Write("Arrows select  Enter edits  Space toggles  F10/S saves  Esc cancels\n");
        if (status.Length > 0) terminal.Write($"\n{status}\n");
    }

    private DmRouteSettings Edit(DmRouteSettings settings, Field field, out string status)
    {
        if (field == Field.LocationPrivacyEnabled)
        {
            status = string.Empty;
            return settings with
            {
                Mqtt = settings.Mqtt with
                {
                    LocationPrivacyEnabled = !settings.Mqtt.LocationPrivacyEnabled
                }
            };
        }

        var secret = field is Field.ZonePsk or Field.MeshPsk;
        var input = terminal.ReadValue($"\n{Label(field)}", CurrentValue(settings, field), secret);
        if (input is null)
        {
            status = "Edit cancelled.";
            return settings;
        }

        if (input.Length == 0)
        {
            status = secret ? "Existing secret retained." : "Value unchanged.";
            return settings;
        }

        try
        {
            status = string.Empty;
            return Apply(settings, field, input);
        }
        catch (FormatException)
        {
            status = $"Invalid value for {Label(field)}.";
            return settings;
        }
    }

    private bool TrySave(string path, DmRouteSettings settings, out string status)
    {
        try
        {
            settings.Validate();
        }
        catch (ConfigurationValidationException exception)
        {
            status = exception.Message;
            return false;
        }

        if (File.Exists(path))
        {
            terminal.Write("\nOverwrite the existing configuration file? [y/N] ");
            var answer = terminal.ReadKey();
            terminal.Write("\n");
            if (answer.Key != ConsoleKey.Y)
            {
                status = "Save cancelled; the existing file was not changed.";
                return false;
            }
        }

        try
        {
            ConfigurationFile.SaveAtomic(path, settings);
            status = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            status = $"Could not save configuration: {exception.Message}";
            return false;
        }
    }

    private static string Label(Field field) => field switch
    {
        Field.ZoneId => "Zone ID",
        Field.ZonePsk => "Zone PSK",
        Field.MeshPsk => "Mesh PSK",
        Field.MqttHost => "MQTT host",
        Field.MqttPort => "MQTT port",
        Field.LocationPrivacyEnabled => "Location privacy",
        Field.LocationGridKm => "Location grid (km)",
        Field.MaxActiveCalls => "Max active calls",
        Field.MaxLocalDeviceRoutes => "Max local device routes",
        Field.MaxSdsSessions => "Max SDS sessions",
        Field.MaxSdsMessageBytes => "Max SDS message bytes",
        Field.StateIntervalSeconds => "MQTT state interval (s)",
        Field.EventCapacity => "MQTT event capacity",
        Field.DiagnosticFrameCapacity => "Diagnostic frame capacity",
        Field.MaxDiagnosticFrameBytes => "Max diagnostic frame bytes",
        Field.PayloadBufferBytes => "MQTT payload buffer bytes",
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };

    private static string DisplayValue(DmRouteSettings settings, Field field) => field switch
    {
        Field.ZonePsk or Field.MeshPsk => "********",
        Field.LocationPrivacyEnabled => settings.Mqtt.LocationPrivacyEnabled ? "[*] enabled" : "[ ] disabled",
        _ => CurrentValue(settings, field)
    };

    private static string CurrentValue(DmRouteSettings settings, Field field) => field switch
    {
        Field.ZoneId => settings.ZoneId.ToString(CultureInfo.InvariantCulture),
        Field.ZonePsk => settings.ZonePsk,
        Field.MeshPsk => settings.MeshPsk,
        Field.MqttHost => settings.Mqtt.Host,
        Field.MqttPort => settings.Mqtt.Port.ToString(CultureInfo.InvariantCulture),
        Field.LocationPrivacyEnabled => settings.Mqtt.LocationPrivacyEnabled.ToString(CultureInfo.InvariantCulture),
        Field.LocationGridKm => settings.Mqtt.LocationGridKm.ToString(CultureInfo.InvariantCulture),
        Field.MaxActiveCalls => settings.Routing.MaxActiveCalls.ToString(CultureInfo.InvariantCulture),
        Field.MaxLocalDeviceRoutes => settings.Routing.MaxLocalDeviceRoutes.ToString(CultureInfo.InvariantCulture),
        Field.MaxSdsSessions => settings.Sds.MaxSessions.ToString(CultureInfo.InvariantCulture),
        Field.MaxSdsMessageBytes => settings.Sds.MaxMessageBytes.ToString(CultureInfo.InvariantCulture),
        Field.StateIntervalSeconds => settings.Mqtt.StateIntervalSeconds.ToString(CultureInfo.InvariantCulture),
        Field.EventCapacity => settings.Mqtt.EventCapacity.ToString(CultureInfo.InvariantCulture),
        Field.DiagnosticFrameCapacity => settings.Mqtt.DiagnosticFrameCapacity.ToString(CultureInfo.InvariantCulture),
        Field.MaxDiagnosticFrameBytes => settings.Mqtt.MaxDiagnosticFrameBytes.ToString(CultureInfo.InvariantCulture),
        Field.PayloadBufferBytes => settings.Mqtt.PayloadBufferBytes.ToString(CultureInfo.InvariantCulture),
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };

    private static DmRouteSettings Apply(DmRouteSettings settings, Field field, string input) => field switch
    {
        Field.ZoneId => settings with { ZoneId = ParseInt(input) },
        Field.ZonePsk => settings with { ZonePsk = input },
        Field.MeshPsk => settings with { MeshPsk = input },
        Field.MqttHost => settings with { Mqtt = settings.Mqtt with { Host = input } },
        Field.MqttPort => settings with { Mqtt = settings.Mqtt with { Port = ParseInt(input) } },
        Field.LocationGridKm => settings with { Mqtt = settings.Mqtt with { LocationGridKm = ParseDouble(input) } },
        Field.MaxActiveCalls => settings with { Routing = settings.Routing with { MaxActiveCalls = ParseInt(input) } },
        Field.MaxLocalDeviceRoutes => settings with { Routing = settings.Routing with { MaxLocalDeviceRoutes = ParseInt(input) } },
        Field.MaxSdsSessions => settings with { Sds = settings.Sds with { MaxSessions = ParseInt(input) } },
        Field.MaxSdsMessageBytes => settings with { Sds = settings.Sds with { MaxMessageBytes = ParseInt(input) } },
        Field.StateIntervalSeconds => settings with { Mqtt = settings.Mqtt with { StateIntervalSeconds = ParseInt(input) } },
        Field.EventCapacity => settings with { Mqtt = settings.Mqtt with { EventCapacity = ParseInt(input) } },
        Field.DiagnosticFrameCapacity => settings with { Mqtt = settings.Mqtt with { DiagnosticFrameCapacity = ParseInt(input) } },
        Field.MaxDiagnosticFrameBytes => settings with { Mqtt = settings.Mqtt with { MaxDiagnosticFrameBytes = ParseInt(input) } },
        Field.PayloadBufferBytes => settings with { Mqtt = settings.Mqtt with { PayloadBufferBytes = ParseInt(input) } },
        _ => settings
    };

    private static int ParseInt(string input) => int.Parse(input, NumberStyles.Integer, CultureInfo.InvariantCulture);
    private static double ParseDouble(string input) => double.Parse(input, NumberStyles.Float, CultureInfo.InvariantCulture);
}
