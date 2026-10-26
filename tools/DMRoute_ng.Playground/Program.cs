namespace DMRoute_ng.Playground;

internal static class Program
{
    public static Task<int> Main(string[] args) => PlaygroundApplication.RunAsync(args);
}

internal static class PlaygroundApplication
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (!PlaygroundStartupArguments.TryParse(args, out var startup, out var argumentError))
        {
            Console.Error.WriteLine($"Argumentfehler: {argumentError}");
            WriteUsage(Console.Error);
            return 2;
        }

        if (startup.Help)
        {
            WriteUsage(Console.Out);
            return 0;
        }

        if (startup.Command.Length > 0)
        {
            using var cancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler handler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };
            Console.CancelKeyPress += handler;
            try
            {
                return await VoiceCommand.RunAsync(startup, cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                Console.Error.WriteLine("Sprachverarbeitung abgebrochen.");
                return 130;
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException or
                                               UnauthorizedAccessException or InvalidOperationException or
                                               System.ComponentModel.Win32Exception or PlaygroundConfigurationException or
                                               ScenarioFormatException)
            {
                Console.Error.WriteLine($"Sprachfehler: {exception.Message}");
                return 2;
            }
            finally
            {
                Console.CancelKeyPress -= handler;
            }
        }

        if (startup.Setup)
        {
            try
            {
                var result = new PlaygroundSetupWizard(new ConsolePlaygroundSetupTerminal()).Run(startup.ConfigPath);
                return result == PlaygroundSetupResult.Error ? 2 : 0;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               PlaygroundConfigurationException)
            {
                Console.Error.WriteLine($"Setup-Fehler: {exception.Message}");
                return 2;
            }
        }

        try
        {
            var settings = PlaygroundConfigurationFile.Load(startup.ConfigPath);
            using var applicationCancellation = new CancellationTokenSource();
            var masterEndPoint = await settings.ResolveMasterAsync(applicationCancellation.Token).ConfigureAwait(false);
            await using var repl = new PlaygroundRepl(settings, masterEndPoint);

            ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                if (!repl.CancelCurrentOperation()) repl.RequestExit();
            };
            Console.CancelKeyPress += cancelHandler;
            try
            {
                if (!Console.IsInputRedirected && !Console.IsOutputRedirected)
                    await repl.RunConsoleAsync(applicationCancellation.Token).ConfigureAwait(false);
                else
                    await repl.RunAsync(Console.In, Console.Out, applicationCancellation.Token).ConfigureAwait(false);
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
            }
            return 0;
        }
        catch (Exception exception) when (exception is PlaygroundConfigurationException or IOException or
                                           UnauthorizedAccessException or System.Net.Sockets.SocketException)
        {
            Console.Error.WriteLine($"Konfigurationsfehler: {exception.Message}");
            return 2;
        }
    }

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("Verwendung:");
        writer.WriteLine("  DMRoute-ng-playground --setup [--config <pfad>]");
        writer.WriteLine("  DMRoute-ng-playground [--config <pfad>]");
        writer.WriteLine("  DMRoute-ng-playground [--config <pfad>] [--open-dmr <pfad>] voice info");
        writer.WriteLine("  DMRoute-ng-playground [--config <pfad>] [--open-dmr <pfad>] voice decode <eingabe.dmrd|eingabe.ambe> <ausgabe.wav>");
        writer.WriteLine("  DMRoute-ng-playground --config <pfad> [--open-dmr <pfad>] voice encode <radio> <tg|fug> <ziel-id> <eingabe.wav> <ausgabe.dmrd>");
    }
}
