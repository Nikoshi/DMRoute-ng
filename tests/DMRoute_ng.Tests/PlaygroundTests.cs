using System.Buffers.Binary;
using DMRoute_ng.Emulator;
using DMRoute_ng.Playground;

namespace DMRoute_ng.Tests;

public sealed class PlaygroundTests
{
    private const string CapturedFrame =
        "444D5244050027110F1B93000F4241A6A3E1BE9413ED97E6073B088BA733CF8F058D5D7F77FD757338C1C05A79680661090210199A";

    [Fact]
    public void StartupArguments_ParseSetupConfigAndRejectUnknownValues()
    {
        Assert.True(PlaygroundStartupArguments.TryParse(
            ["--setup", "--config", "custom.json"], out var parsed, out var error));
        Assert.Null(error);
        Assert.True(parsed.Setup);
        Assert.Equal("custom.json", parsed.ConfigPath);

        Assert.False(PlaygroundStartupArguments.TryParse(["--wat"], out _, out error));
        Assert.Contains("Unbekanntes", error);
    }

    [Fact]
    public void CommandTokenizer_PreservesQuotedScenarioPaths()
    {
        Assert.True(CommandLineTokenizer.TryTokenize(
            "run hs1 \"captures/my scenario.dmrd\"", out var tokens, out var error));
        Assert.Null(error);
        Assert.Equal(["run", "hs1", "captures/my scenario.dmrd"], tokens);

        Assert.False(CommandLineTokenizer.TryTokenize("run hs1 \"open", out _, out error));
        Assert.Contains("Nicht abgeschlossene", error);
    }

    [Fact]
    public void CommandCompletion_UsesGrammarConfiguredNamesAndServiceId()
    {
        var completer = new PlaygroundCommandCompleter(["hs1", "hs2"], ["radio1", "radio2"], [9, 262]);

        Assert.Equal(["sds"], completer.Complete("sd", 2).Candidates);
        Assert.Equal(["radio1", "radio2"], completer.Complete("sds send rad", 12).Candidates);
        Assert.Equal(["service"], completer.Complete("sds send radio1 ser", 19).Candidates);
        Assert.Equal(["990099"], completer.Complete("sds send radio1 service 99", 28).Candidates);
        Assert.Contains("hs2", completer.Complete("login h", 7).Candidates);
    }

    [Fact]
    public void CommandHistory_NavigatesAndSearchesWithoutPersistingDuplicates()
    {
        var history = new PlaygroundCommandHistory(3);
        history.Add("status");
        history.Add("status");
        history.Add("radio list");
        history.Add("sds inbox radio1");

        Assert.Equal("sds inbox radio1", history.Previous("draft"));
        Assert.Equal("radio list", history.Previous("sds inbox radio1"));
        Assert.Equal("sds inbox radio1", history.Next("radio list"));
        Assert.Equal("draft", history.Next("sds inbox radio1"));
        Assert.Equal(2, history.FindPrevious("inbox", history.Entries.Count));
        Assert.Equal(1, history.FindPrevious("radio", 2));
    }

    [Fact]
    public void Settings_RejectDuplicateNamesAndIds()
    {
        var settings = ValidSettings() with
        {
            Hotspots =
            [
                Hotspot("HS", 1000001),
                Hotspot("hs", 1000002)
            ],
            Radios = []
        };
        Assert.Throws<PlaygroundConfigurationException>(settings.Validate);

        settings = ValidSettings() with
        {
            Radios =
            [
                new PlaygroundRadioSettings { Name = "radio", RadioId = 10001, Hotspot = "hs1" },
                new PlaygroundRadioSettings { Name = "RADIO", RadioId = 10002, Hotspot = "hs1" }
            ]
        };
        Assert.Throws<PlaygroundConfigurationException>(settings.Validate);

        settings = ValidSettings() with
        {
            Radios = [new PlaygroundRadioSettings { Name = "radio", RadioId = 10001, Hotspot = "missing" }]
        };
        Assert.Throws<PlaygroundConfigurationException>(settings.Validate);

        settings = ValidSettings() with
        {
            Hotspots =
            [
                Hotspot("one", 1000001),
                Hotspot("two", 1000001)
            ],
            Radios = []
        };
        Assert.Throws<PlaygroundConfigurationException>(settings.Validate);

        settings = ValidSettings() with
        {
            Hotspots = Enumerable.Range(1, 65)
                .Select(index => Hotspot($"hs{index}", 1000000 + index))
                .ToArray(),
            Radios = []
        };
        Assert.Throws<PlaygroundConfigurationException>(settings.Validate);
    }

    [Fact]
    public void Configuration_RoundTripsAndUsesRestrictedUnixPermissions()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dmroute-playground-{Guid.NewGuid():N}.json");
        try
        {
            PlaygroundConfigurationFile.SaveAtomic(path, ValidSettings());
            var loaded = PlaygroundConfigurationFile.Load(path);

            Assert.Equal("127.0.0.1", loaded.Master.Host);
            Assert.Equal("secret1000001", loaded.Hotspots[0].PreSharedKey);
            Assert.Equal(10001, loaded.Radios[0].RadioId);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SetupWizard_DerivesFullHotspotPassword()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dmroute-playground-{Guid.NewGuid():N}.json");
        var terminal = new ScriptedSetupTerminal(
            ["", "", "1", "", "", "", "", "", "", "", "", "", "", "", ""],
            ["zone-secret"]);

        try
        {
            var result = new PlaygroundSetupWizard(terminal).Run(path);
            var loaded = PlaygroundConfigurationFile.Load(path);

            Assert.Equal(PlaygroundSetupResult.Saved, result);
            Assert.Equal("zone-secret1000001", loaded.Hotspots[0].PreSharedKey);
            Assert.Single(loaded.Radios);
            Assert.Equal(10001, loaded.Radios[0].RadioId);
            Assert.Equal("hs1", loaded.Radios[0].Hotspot);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ScenarioFile_ParsesFramesAndNormalizesRepeaterId()
    {
        var path = WriteScenario($"# sample\nname=Captured frame\nradioId=10001\n60 {CapturedFrame}\n");
        try
        {
            var loaded = ScenarioFile.Load(path, 1000099);

            Assert.Equal("Captured frame", loaded.Scenario.Name);
            Assert.Equal(10001, loaded.Scenario.RadioId);
            Assert.Single(loaded.Scenario.Steps);
            Assert.Equal(TimeSpan.FromMilliseconds(60), loaded.Scenario.Steps[0].DelayBefore);
            Assert.Equal(1, loaded.RewrittenRepeaterIds);
            Assert.Equal(1000099,
                BinaryPrimitives.ReadInt32BigEndian(loaded.Scenario.Steps[0].Packet.Span[11..15]));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("name=x\nradioId=10001\n-1 00\n")]
    [InlineData("name=x\nradioId=10001\n0 xyz\n")]
    [InlineData("radioId=10001\n0 00\n")]
    [InlineData("name=x\n0 00\n")]
    public void ScenarioFile_RejectsMalformedInput(string contents)
    {
        var path = WriteScenario(contents);
        try
        {
            Assert.Throws<ScenarioFormatException>(() => ScenarioFile.Load(path, 1000001));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ScenarioFile_RejectsFramesFromAnotherRadio()
    {
        var path = WriteScenario($"name=wrong source\nradioId=10002\n0 {CapturedFrame}\n");
        try
        {
            var exception = Assert.Throws<ScenarioFormatException>(() => ScenarioFile.Load(path, 1000001));
            Assert.Contains("source ID", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ScenarioFile_EnforcesStepLimit()
    {
        var contents = new System.Text.StringBuilder("name=too many\nradioId=10001\n");
        for (var index = 0; index <= 10_000; index++) contents.Append("0 ").Append(CapturedFrame).Append('\n');
        var path = WriteScenario(contents.ToString());
        try
        {
            var exception = Assert.Throws<ScenarioFormatException>(() => ScenarioFile.Load(path, 1000001));
            Assert.Contains("höchstens 10000", exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Repl_StatusAndErrorsNeverPrintSecrets()
    {
        await using var repl = new PlaygroundRepl(
            ValidSettings(), new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 62031));
        var output = new StringWriter();

        await repl.RunAsync(new StringReader(
            "status\nradio list\nsds inbox radio1\nsds clear radio1\nunknown\nexit\n"), output);

        Assert.Contains("hs1: ID 1000001", output.ToString());
        Assert.Contains("radio1: FuG 10001", output.ToString());
        Assert.Contains("radio1: Inbox leer", output.ToString());
        Assert.Contains("radio1: 0 SDS entfernt", output.ToString());
        Assert.Contains("Unbekannter Befehl", output.ToString());
        Assert.DoesNotContain("secret1000001", output.ToString());
    }

    [Fact]
    public async Task Repl_WatchCanBeCancelledWithoutEndingSession()
    {
        await using var repl = new PlaygroundRepl(
            ValidSettings(), new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 62031));
        var output = new StringWriter();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var run = repl.RunAsync(new StringReader("watch hs1\nexit\n"), output, timeout.Token);

        var cancelled = false;
        for (var attempt = 0; attempt < 100 && !cancelled; attempt++)
        {
            await Task.Delay(10, timeout.Token);
            cancelled = repl.CancelCurrentOperation();
        }
        await run;

        Assert.True(cancelled);
        Assert.Contains("Aktion abgebrochen", output.ToString());
        Assert.Contains("Playground wird beendet", output.ToString());
    }

    private static PlaygroundSettings ValidSettings() => new()
    {
        Master = new PlaygroundMasterSettings(),
        Hotspots = [Hotspot("hs1", 1000001)],
        Radios = [new PlaygroundRadioSettings { Name = "radio1", RadioId = 10001, Hotspot = "hs1" }]
    };

    private static PlaygroundHotspotSettings Hotspot(string name, int repeaterId) => new()
    {
        Name = name,
        RepeaterId = repeaterId,
        PreSharedKey = $"secret{repeaterId}"
    };

    private static string WriteScenario(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dmroute-scenario-{Guid.NewGuid():N}.dmrd");
        File.WriteAllText(path, contents.Replace("\\n", Environment.NewLine, StringComparison.Ordinal));
        return path;
    }

    private sealed class ScriptedSetupTerminal(
        IEnumerable<string?> lines,
        IEnumerable<string?> secrets) : IPlaygroundSetupTerminal
    {
        private readonly Queue<string?> _lines = new(lines);
        private readonly Queue<string?> _secrets = new(secrets);
        public bool IsInteractive => true;
        public void Write(string text) { }
        public string? ReadLine() => _lines.Dequeue();
        public string? ReadSecret(string prompt) => _secrets.Dequeue();
    }
}
