using System.Text.Json;
using DMRoute_ng.Configuration;

namespace DMRoute_ng.Tests;

public sealed class SetupWizardTests
{
    [Fact]
    public void Save_WritesCompleteConfigurationAndMasksSecrets()
    {
        var directory = ConfigurationTests.CreateTemporaryDirectory();
        var path = Path.Combine(directory, "dmroute.json");
        var terminal = new ScriptedTerminal([Key(ConsoleKey.F10)]);

        try
        {
            var result = new SetupWizard(terminal).Run(path, ConfigurationTests.ValidSettings());

            Assert.Equal(SetupResult.Saved, result);
            Assert.DoesNotContain("zone-secret", terminal.Output);
            Assert.DoesNotContain("mesh-secret", terminal.Output);
            var saved = JsonSerializer.Deserialize(
                File.ReadAllText(path), DmRouteJsonContext.Default.DmRouteSettings);
            Assert.Equal(ConfigurationTests.ValidSettings(), saved);
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AdvancedField_CanBeEditedAndExistingFileRequiresConfirmation()
    {
        var directory = ConfigurationTests.CreateTemporaryDirectory();
        var path = Path.Combine(directory, "dmroute.json");
        ConfigurationFile.SaveAtomic(path, ConfigurationTests.ValidSettings());
        var keys = new List<ConsoleKeyInfo> { Key(ConsoleKey.A) };
        for (var i = 0; i < 7; i++) keys.Add(Key(ConsoleKey.DownArrow));
        keys.Add(Key(ConsoleKey.Enter));
        keys.Add(Key(ConsoleKey.F10));
        keys.Add(Key(ConsoleKey.Y, 'y'));
        var terminal = new ScriptedTerminal(keys, ["512"]);

        try
        {
            var result = new SetupWizard(terminal).Run(path, ConfigurationFile.LoadOrDefault(path));

            Assert.Equal(SetupResult.Saved, result);
            Assert.Contains("Advanced settings", terminal.Output);
            var saved = JsonSerializer.Deserialize(
                File.ReadAllText(path), DmRouteJsonContext.Default.DmRouteSettings);
            Assert.Equal(512, saved!.Routing.MaxActiveCalls);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Escape_LeavesExistingFileUnchanged()
    {
        var directory = ConfigurationTests.CreateTemporaryDirectory();
        var path = Path.Combine(directory, "dmroute.json");
        File.WriteAllText(path, "original");
        var terminal = new ScriptedTerminal([Key(ConsoleKey.Escape)]);

        try
        {
            var result = new SetupWizard(terminal).Run(path, ConfigurationTests.ValidSettings());

            Assert.Equal(SetupResult.Cancelled, result);
            Assert.Equal("original", File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void NonInteractiveTerminal_FailsCleanly()
    {
        var terminal = new ScriptedTerminal([], interactive: false);

        var result = new SetupWizard(terminal).Run("unused.json", ConfigurationTests.ValidSettings());

        Assert.Equal(SetupResult.Error, result);
        Assert.Contains("interactive terminal", terminal.Output);
    }

    private static ConsoleKeyInfo Key(ConsoleKey key, char character = '\0') =>
        new(character, key, shift: false, alt: false, control: false);

    private sealed class ScriptedTerminal(
        IEnumerable<ConsoleKeyInfo> keys,
        IEnumerable<string>? values = null,
        bool interactive = true) : ISetupTerminal
    {
        private readonly Queue<ConsoleKeyInfo> _keys = new(keys);
        private readonly Queue<string> _values = new(values ?? []);
        private readonly StringWriter _output = new();

        public bool IsInteractive { get; } = interactive;
        public string Output => _output.ToString();
        public void Clear() => _output.WriteLine("<clear>");
        public void Write(string text) => _output.Write(text);
        public ConsoleKeyInfo ReadKey() => _keys.Dequeue();
        public string? ReadValue(string prompt, string currentValue, bool secret)
        {
            _output.Write(prompt);
            return _values.Dequeue();
        }
    }
}
