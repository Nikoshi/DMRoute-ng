using System.Globalization;
using System.Net;
using System.Net.Sockets;
using DMRoute_ng.Emulator;

namespace DMRoute_ng.Playground;

internal sealed class PlaygroundRepl : IAsyncDisposable
{
    private sealed record HotspotSession(
        PlaygroundHotspotSettings Settings,
        VirtualHotspot Hotspot);

    private sealed record RadioSession(
        PlaygroundRadioSettings Settings,
        VirtualRadio Radio,
        HotspotSession Hotspot);

    private readonly IPEndPoint _masterEndPoint;
    private readonly Dictionary<string, HotspotSession> _hotspots;
    private readonly Dictionary<string, RadioSession> _radios;
    private readonly CancellationTokenSource _exit = new();
    private readonly object _operationLock = new();
    private CancellationTokenSource? _currentOperation;
    private int _disposed;

    public PlaygroundRepl(PlaygroundSettings settings, IPEndPoint masterEndPoint)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(masterEndPoint);
        _masterEndPoint = masterEndPoint;
        _hotspots = new Dictionary<string, HotspotSession>(StringComparer.OrdinalIgnoreCase);
        foreach (var hotspotSettings in settings.Hotspots)
        {
            var hotspot = new VirtualHotspot(hotspotSettings.ToOptions(masterEndPoint));
            _hotspots.Add(hotspotSettings.Name, new HotspotSession(hotspotSettings, hotspot));
        }
        _radios = new Dictionary<string, RadioSession>(StringComparer.OrdinalIgnoreCase);
        foreach (var radioSettings in settings.Radios)
        {
            var hotspot = _hotspots[radioSettings.Hotspot];
            var radio = new VirtualRadio(hotspot.Hotspot,
                radioSettings.ToOptions(hotspot.Settings.Configuration.ColorCode));
            _radios.Add(radioSettings.Name, new RadioSession(radioSettings, radio, hotspot));
        }
    }

    public bool CancelCurrentOperation()
    {
        lock (_operationLock)
        {
            if (_currentOperation is null) return false;
            _currentOperation.Cancel();
            return true;
        }
    }

    public void RequestExit() => _exit.Cancel();

    public Task RunAsync(TextReader input, TextWriter output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        return RunCoreAsync(input, output, null, cancellationToken);
    }

    public Task RunConsoleAsync(CancellationToken cancellationToken = default)
    {
        var completer = new PlaygroundCommandCompleter(
            _hotspots.Keys, _radios.Keys, _radios.Values.SelectMany(static session => session.Radio.Talkgroups));
        return RunCoreAsync(null, Console.Out, new PlaygroundConsoleLineEditor(completer), cancellationToken);
    }

    private async Task RunCoreAsync(
        TextReader? input,
        TextWriter output,
        PlaygroundConsoleLineEditor? lineEditor,
        CancellationToken cancellationToken)
    {
        using var linkedExit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _exit.Token);

        await output.WriteLineAsync("DMRoute-ng Emulator-Playground").ConfigureAwait(false);
        await output.WriteLineAsync($"Master: {_masterEndPoint.Address}:{_masterEndPoint.Port}").ConfigureAwait(false);
        await output.WriteLineAsync(
            "'help' zeigt alle Befehle. Tab ergänzt, ↑/↓ navigiert und Ctrl+R durchsucht den Verlauf.")
            .ConfigureAwait(false);
        await output.WriteLineAsync("Ctrl+C bricht die aktuelle Aktion ab.").ConfigureAwait(false);

        while (!linkedExit.IsCancellationRequested)
        {
            string? line;
            try
            {
                if (lineEditor is null)
                {
                    await output.WriteAsync("playground> ").ConfigureAwait(false);
                    await output.FlushAsync().ConfigureAwait(false);
                    line = await input!.ReadLineAsync(linkedExit.Token).ConfigureAwait(false);
                }
                else line = lineEditor.ReadLine("playground> ", linkedExit.Token);
            }
            catch (OperationCanceledException) when (linkedExit.IsCancellationRequested)
            {
                break;
            }

            if (line is null) break;
            if (!CommandLineTokenizer.TryTokenize(line, out var tokens, out var parseError))
            {
                await output.WriteLineAsync($"Fehler: {parseError}").ConfigureAwait(false);
                continue;
            }
            if (tokens.Length == 0) continue;
            if (tokens[0].Equals("exit", StringComparison.OrdinalIgnoreCase) ||
                tokens[0].Equals("quit", StringComparison.OrdinalIgnoreCase))
                break;

            using var operation = CancellationTokenSource.CreateLinkedTokenSource(linkedExit.Token);
            lock (_operationLock) _currentOperation = operation;
            try
            {
                await ExecuteAsync(tokens, output, operation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (operation.IsCancellationRequested && !linkedExit.IsCancellationRequested)
            {
                await output.WriteLineAsync("Aktion abgebrochen.").ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                               HomebrewProtocolException or ScenarioFormatException or IOException or
                                               UnauthorizedAccessException or SocketException or TimeoutException)
            {
                await output.WriteLineAsync($"Fehler: {exception.Message}").ConfigureAwait(false);
            }
            finally
            {
                lock (_operationLock)
                {
                    if (ReferenceEquals(_currentOperation, operation)) _currentOperation = null;
                }
            }
        }

        await output.WriteLineAsync("Playground wird beendet.").ConfigureAwait(false);
    }

    private async Task ExecuteAsync(string[] tokens, TextWriter output, CancellationToken cancellationToken)
    {
        var command = tokens[0].ToLowerInvariant();
        switch (command)
        {
            case "help":
            case "?":
                RequireCount(tokens, 1, "help");
                await WriteHelpAsync(output).ConfigureAwait(false);
                return;
            case "status":
            case "st":
                RequireCount(tokens, 1, "status");
                await WriteStatusAsync(output).ConfigureAwait(false);
                return;
            case "login":
            case "l":
                RequireCount(tokens, 2, "login <name|all>");
                await ForTargetsAsync(tokens[1], output, cancellationToken, static (session, token) =>
                    session.Hotspot.LoginAsync(token), "angemeldet").ConfigureAwait(false);
                return;
            case "ping":
            case "p":
                RequireCount(tokens, 2, "ping <name|all>");
                await ForTargetsAsync(tokens[1], output, cancellationToken, static (session, token) =>
                    session.Hotspot.PingAsync(token), "Pong empfangen").ConfigureAwait(false);
                return;
            case "disconnect":
            case "dc":
                RequireCount(tokens, 2, "disconnect <name|all>");
                await ForTargetsAsync(tokens[1], output, cancellationToken, static (session, token) =>
                    session.Hotspot.DisconnectAsync(token), "abgemeldet").ConfigureAwait(false);
                return;
            case "send":
            case "s":
                RequireCount(tokens, 3, "send <name> <DMRD-hex>");
                await SendAsync(tokens[1], tokens[2], output, cancellationToken).ConfigureAwait(false);
                return;
            case "run":
            case "r":
                RequireCount(tokens, 3, "run <name> <scenario-file>");
                await RunScenarioAsync(tokens[1], tokens[2], output, cancellationToken).ConfigureAwait(false);
                return;
            case "receive":
            case "rv":
                if (tokens.Length is < 2 or > 3)
                    throw new ArgumentException("Verwendung: receive <name> [timeout-ms]");
                await ReceiveAsync(tokens[1], tokens.Length == 3 ? tokens[2] : null, output, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case "watch":
            case "w":
                RequireCount(tokens, 2, "watch <name>");
                await WatchAsync(tokens[1], output, cancellationToken).ConfigureAwait(false);
                return;
            case "clear":
            case "cls":
                RequireCount(tokens, 2, "clear <name>");
                await ClearAsync(tokens[1], output).ConfigureAwait(false);
                return;
            case "radio":
                await RadioCommandAsync(tokens, output, cancellationToken).ConfigureAwait(false);
                return;
            case "sds":
                await SdsCommandAsync(tokens, output, cancellationToken).ConfigureAwait(false);
                return;
            default:
                throw new ArgumentException($"Unbekannter Befehl '{tokens[0]}'. 'help' zeigt alle Befehle.");
        }
    }

    private static async Task WriteHelpAsync(TextWriter output)
    {
        await output.WriteLineAsync("Befehle:").ConfigureAwait(false);
        await output.WriteLineAsync("  st  status").ConfigureAwait(false);
        await output.WriteLineAsync("  l   login <name|all>").ConfigureAwait(false);
        await output.WriteLineAsync("  p   ping <name|all>").ConfigureAwait(false);
        await output.WriteLineAsync("  dc  disconnect <name|all>").ConfigureAwait(false);
        await output.WriteLineAsync("  s   send <name> <DMRD-hex>").ConfigureAwait(false);
        await output.WriteLineAsync("  r   run <name> <scenario-file>").ConfigureAwait(false);
        await output.WriteLineAsync("  rv  receive <name> [timeout-ms]").ConfigureAwait(false);
        await output.WriteLineAsync("  w   watch <name>   (Ctrl+C beendet die Beobachtung)").ConfigureAwait(false);
        await output.WriteLineAsync("  cls clear <name>").ConfigureAwait(false);
        await output.WriteLineAsync("      radio list").ConfigureAwait(false);
        await output.WriteLineAsync("      radio announce <name|all>").ConfigureAwait(false);
        await output.WriteLineAsync("      sds send <radio> <tg|fug|service> <ziel-id> \"<text>\"").ConfigureAwait(false);
        await output.WriteLineAsync("      sds inbox <radio>").ConfigureAwait(false);
        await output.WriteLineAsync("      sds watch <radio>   (Ctrl+C beendet die Beobachtung)").ConfigureAwait(false);
        await output.WriteLineAsync("      sds clear <radio>").ConfigureAwait(false);
        await output.WriteLineAsync("      exit").ConfigureAwait(false);
    }

    private async Task WriteStatusAsync(TextWriter output)
    {
        await output.WriteLineAsync($"Master {_masterEndPoint.Address}:{_masterEndPoint.Port}").ConfigureAwait(false);
        foreach (var session in _hotspots.Values)
        {
            await output.WriteLineAsync(
                $"{session.Settings.Name}: ID {session.Hotspot.RepeaterId}, " +
                $"{session.Hotspot.State}, lokal {session.Hotspot.LocalEndPoint.Address}:{session.Hotspot.LocalEndPoint.Port}, " +
                $"Raw-DMRD verworfen {session.Hotspot.DroppedDmrdPackets}")
                .ConfigureAwait(false);
        }
        foreach (var session in _radios.Values)
            await WriteRadioStatusAsync(session, output).ConfigureAwait(false);
    }

    private async Task RadioCommandAsync(string[] tokens, TextWriter output, CancellationToken cancellationToken)
    {
        if (tokens.Length == 2 && tokens[1].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            if (_radios.Count == 0) await output.WriteLineAsync("Keine virtuellen Funkgeräte konfiguriert.").ConfigureAwait(false);
            foreach (var radio in _radios.Values) await WriteRadioStatusAsync(radio, output).ConfigureAwait(false);
            return;
        }
        if (tokens.Length == 3 && tokens[1].Equals("announce", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var session in ResolveRadioTargets(tokens[2]))
            {
                try
                {
                    await session.Radio.AnnounceAsync(cancellationToken).ConfigureAwait(false);
                    await output.WriteLineAsync($"{session.Settings.Name}: privater Routingpfad bereit.").ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is InvalidOperationException or TimeoutException)
                {
                    await output.WriteLineAsync($"{session.Settings.Name}: Fehler: {exception.Message}").ConfigureAwait(false);
                }
            }
            return;
        }
        throw new ArgumentException("Verwendung: radio <list|announce <name|all>>");
    }

    private async Task SdsCommandAsync(string[] tokens, TextWriter output, CancellationToken cancellationToken)
    {
        if (tokens.Length == 6 && tokens[1].Equals("send", StringComparison.OrdinalIgnoreCase))
        {
            var session = ResolveRadio(tokens[2]);
            var kindText = tokens[3].ToLowerInvariant();
            var kind = kindText switch
            {
                "tg" or "service" => SdsTargetKind.Talkgroup,
                "fug" or "radio" or "private" => SdsTargetKind.Radio,
                _ => throw new ArgumentException("Zielart muss tg, fug oder service sein.")
            };
            if (!int.TryParse(tokens[4], NumberStyles.None, CultureInfo.InvariantCulture, out var destinationId))
                throw new ArgumentException("ziel-id muss eine positive ganze Zahl sein.");
            if (kindText == "service" && destinationId is < 990000 or > 999999)
                throw new ArgumentException("Service-IDs müssen zwischen 990000 und 999999 liegen.");
            if (kind == SdsTargetKind.Radio && destinationId is >= 990000 and <= 999999)
                throw new ArgumentException(
                    $"{destinationId} ist eine Service-ID; bitte 'service {destinationId}' oder 'tg {destinationId}' verwenden.");
            var result = await session.Radio.SendSdsAsync(kind, destinationId, tokens[5], cancellationToken)
                .ConfigureAwait(false);
            var resultText = result.Status switch
            {
                SdsSendStatus.Sent => "unbestätigt gesendet",
                SdsSendStatus.Acknowledged => $"nach {result.Attempts} Versuch(en) bestätigt",
                SdsSendStatus.Rejected => "mit NACK abgelehnt",
                _ => $"nach {result.Attempts} Versuch(en) ohne Bestätigung"
            };
            await output.WriteLineAsync($"{session.Settings.Name}: SDS {resultText}.").ConfigureAwait(false);
            return;
        }
        if (tokens.Length == 3 && tokens[1].Equals("inbox", StringComparison.OrdinalIgnoreCase))
        {
            var session = ResolveRadio(tokens[2]);
            var count = 0;
            while (session.Radio.TryReceiveSds(out var message))
            {
                await WriteSdsAsync(session.Settings.Name, message, output).ConfigureAwait(false);
                count++;
            }
            if (count == 0) await output.WriteLineAsync($"{session.Settings.Name}: Inbox leer.").ConfigureAwait(false);
            return;
        }
        if (tokens.Length == 3 && tokens[1].Equals("watch", StringComparison.OrdinalIgnoreCase))
        {
            var session = ResolveRadio(tokens[2]);
            await output.WriteLineAsync($"{session.Settings.Name}: SDS-Beobachtung aktiv; Ctrl+C beendet sie.")
                .ConfigureAwait(false);
            while (session.Radio.TryReceiveSds(out var pending))
                await WriteSdsAsync(session.Settings.Name, pending, output).ConfigureAwait(false);
            while (true)
            {
                var message = await session.Radio.ReceiveSdsAsync(cancellationToken).ConfigureAwait(false);
                await WriteSdsAsync(session.Settings.Name, message, output).ConfigureAwait(false);
            }
        }
        if (tokens.Length == 3 && tokens[1].Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            var session = ResolveRadio(tokens[2]);
            await output.WriteLineAsync($"{session.Settings.Name}: {session.Radio.ClearInbox()} SDS entfernt.")
                .ConfigureAwait(false);
            return;
        }
        throw new ArgumentException(
            "Verwendung: sds <send <radio> <tg|fug|service> <ziel-id> \"<text>\"|inbox <radio>|watch <radio>|clear <radio>>");
    }

    private static Task WriteRadioStatusAsync(RadioSession session, TextWriter output) =>
        output.WriteLineAsync(
            $"{session.Settings.Name}: FuG {session.Radio.RadioId}, Hotspot {session.Settings.Hotspot}, " +
            $"TS {session.Radio.Timeslot}, TGs [{string.Join(',', session.Radio.Talkgroups)}], " +
            $"Route {(session.Radio.IsAnnounced ? "bereit" : "nicht angekündigt")}, " +
            $"Inbox {session.Radio.UnreadMessages}, verworfen {session.Radio.DroppedMessages}");

    private static Task WriteSdsAsync(string name, SdsMessage message, TextWriter output) =>
        output.WriteLineAsync(
            $"{message.ReceivedAt:O} <- {name}: SDS {message.SourceId} -> " +
            $"{(message.IsGroupCall ? "TG" : "FuG")} {message.DestinationId} " +
            $"({(message.IsConfirmed ? "bestätigt" : "unbestätigt")}): {message.Text}");

    private async Task ForTargetsAsync(
        string selector,
        TextWriter output,
        CancellationToken cancellationToken,
        Func<HotspotSession, CancellationToken, Task> action,
        string success)
    {
        foreach (var session in ResolveTargets(selector))
        {
            try
            {
                await action(session, cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync($"{session.Settings.Name}: {success} ({session.Hotspot.State})")
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HomebrewProtocolException or InvalidOperationException or
                                               SocketException)
            {
                await output.WriteLineAsync($"{session.Settings.Name}: Fehler: {exception.Message}")
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task SendAsync(string name, string hex, TextWriter output, CancellationToken cancellationToken)
    {
        var session = ResolveOne(name);
        if (hex.Length == 0 || (hex.Length & 1) != 0 || hex.Length / 2 > 4096)
            throw new ArgumentException("DMRD-hex muss eine gerade Zahl von Hex-Zeichen für höchstens 4096 Bytes enthalten.");
        byte[] packet;
        try
        {
            packet = Convert.FromHexString(hex);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("DMRD-hex enthält ungültige Zeichen.", exception);
        }

        if (!HomebrewProtocol.TryReadDmrdInfo(packet, out var info))
            throw new ArgumentException("Der Wert ist kein vollständiges DMRD-Datagramm.");
        await session.Hotspot.SendDmrdAsync(packet, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync(
            $"{name}: Frame {info.SourceId} -> {info.DestinationId} gesendet.").ConfigureAwait(false);
    }

    private async Task RunScenarioAsync(
        string name,
        string path,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var session = ResolveOne(name);
        var loaded = ScenarioFile.Load(path, session.Hotspot.RepeaterId);
        var radio = new VirtualRadio(loaded.Scenario.RadioId);
        await output.WriteLineAsync(
            $"{name}: Szenario '{loaded.Scenario.Name}' mit {loaded.Scenario.Steps.Count} Frames gestartet; " +
            $"{loaded.RewrittenRepeaterIds} Repeater-IDs normalisiert.").ConfigureAwait(false);
        await radio.ReplayAsync(session.Hotspot, loaded.Scenario, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"{name}: Szenario abgeschlossen.").ConfigureAwait(false);
    }

    private async Task ReceiveAsync(
        string name,
        string? timeoutText,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var session = ResolveOne(name);
        HomebrewDatagram datagram;
        if (timeoutText is null)
        {
            datagram = await session.Hotspot.ReceiveDmrdAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (!int.TryParse(timeoutText, NumberStyles.None, CultureInfo.InvariantCulture, out var timeoutMs) || timeoutMs <= 0)
                throw new ArgumentException("timeout-ms muss eine positive ganze Zahl sein.");
            datagram = await session.Hotspot.ReceiveDmrdAsync(TimeSpan.FromMilliseconds(timeoutMs), cancellationToken)
                .ConfigureAwait(false);
        }
        await WriteDatagramAsync(name, datagram, output).ConfigureAwait(false);
    }

    private async Task WatchAsync(string name, TextWriter output, CancellationToken cancellationToken)
    {
        var session = ResolveOne(name);
        await output.WriteLineAsync($"{name}: Beobachtung aktiv; Ctrl+C beendet sie.").ConfigureAwait(false);
        while (true)
        {
            var datagram = await session.Hotspot.ReceiveDmrdAsync(TimeSpan.FromDays(1), cancellationToken)
                .ConfigureAwait(false);
            await WriteDatagramAsync(name, datagram, output).ConfigureAwait(false);
        }
    }

    private async Task ClearAsync(string name, TextWriter output)
    {
        var session = ResolveOne(name);
        var count = 0;
        while (session.Hotspot.TryReceiveDmrd(out _)) count++;
        await output.WriteLineAsync($"{name}: {count} gepufferte Frames entfernt.").ConfigureAwait(false);
    }

    private static async Task WriteDatagramAsync(string name, HomebrewDatagram datagram, TextWriter output)
    {
        if (!HomebrewProtocol.TryReadDmrdInfo(datagram.Payload, out var info))
        {
            await output.WriteLineAsync(
                $"{datagram.ReceivedAt:O} <- {name}: {Convert.ToHexString(datagram.Payload)}").ConfigureAwait(false);
            return;
        }

        await output.WriteLineAsync(
            $"{datagram.ReceivedAt:O} <- {name}: seq={info.SequenceNumber} src={info.SourceId} " +
            $"dst={info.DestinationId} repeater={info.RepeaterId} slot={info.Timeslot} " +
            $"call={(info.IsGroupCall ? "group" : "private")} data={(info.IsDataFrame ? "yes" : "no")} " +
            $"type=0x{info.DataType:X2} stream=0x{info.StreamId:X8} hex={Convert.ToHexString(datagram.Payload)}")
            .ConfigureAwait(false);
    }

    private IEnumerable<HotspotSession> ResolveTargets(string selector)
    {
        if (selector.Equals("all", StringComparison.OrdinalIgnoreCase)) return _hotspots.Values;
        return [ResolveOne(selector)];
    }

    private HotspotSession ResolveOne(string name)
    {
        if (_hotspots.TryGetValue(name, out var session)) return session;
        throw new ArgumentException($"Unbekannter Hotspot '{name}'.");
    }

    private RadioSession ResolveRadio(string name)
    {
        if (_radios.TryGetValue(name, out var session)) return session;
        throw new ArgumentException($"Unbekanntes Funkgerät '{name}'.");
    }

    private IEnumerable<RadioSession> ResolveRadioTargets(string selector)
    {
        if (selector.Equals("all", StringComparison.OrdinalIgnoreCase)) return _radios.Values;
        return [ResolveRadio(selector)];
    }

    private static void RequireCount(string[] tokens, int expected, string usage)
    {
        if (tokens.Length != expected) throw new ArgumentException($"Verwendung: {usage}");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _exit.Cancel();

        foreach (var session in _radios.Values)
        {
            try { await session.Radio.DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { }
        }

        foreach (var session in _hotspots.Values)
        {
            try
            {
                if (session.Hotspot.State == VirtualHotspotState.Configured)
                    await session.Hotspot.DisconnectAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            try
            {
                await session.Hotspot.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        _exit.Dispose();
    }
}
