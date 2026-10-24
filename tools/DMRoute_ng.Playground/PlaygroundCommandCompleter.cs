namespace DMRoute_ng.Playground;

internal readonly record struct CompletionResult(int Start, int Length, string[] Candidates);

internal sealed class PlaygroundCommandCompleter
{
    private static readonly string[] Commands =
    [
        "help", "status", "login", "ping", "disconnect", "send", "run", "receive", "watch", "clear",
        "radio", "sds", "exit"
    ];

    private readonly string[] _hotspots;
    private readonly string[] _radios;
    private readonly string[] _talkgroups;

    public PlaygroundCommandCompleter(
        IEnumerable<string> hotspots, IEnumerable<string> radios, IEnumerable<int> talkgroups)
    {
        _hotspots = hotspots.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        _radios = radios.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        _talkgroups = talkgroups.Append(990099).Distinct().Order().Select(static id => id.ToString()).ToArray();
    }

    public CompletionResult Complete(string line, int cursor)
    {
        cursor = Math.Clamp(cursor, 0, line.Length);
        var start = FindTokenStart(line, cursor, out var quoted);
        var prefix = line[start..cursor];
        var tokens = TokenizeLoose(line[..start]);
        var candidates = ResolveCandidates(tokens, prefix, quoted)
            .Where(candidate => candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new CompletionResult(start, cursor - start, candidates);
    }

    private IEnumerable<string> ResolveCandidates(IReadOnlyList<string> tokens, string prefix, bool quoted)
    {
        if (tokens.Count == 0) return Commands;
        var command = ExpandCommand(tokens[0]);
        if (tokens.Count == 1)
        {
            if (command is "login" or "ping" or "disconnect") return _hotspots.Append("all");
            if (command is "send" or "run" or "receive" or "watch" or "clear") return _hotspots;
            if (command == "radio") return ["list", "announce", "alert", "check", "events", "watch", "clear"];
            if (command == "sds") return ["send", "inbox", "watch", "clear"];
            return [];
        }

        if (command == "run" && tokens.Count == 2) return CompletePath(prefix, quoted);
        if (command == "radio" && tokens.Count == 2)
        {
            var radioSubcommand = tokens[1].ToLowerInvariant();
            if (radioSubcommand == "announce") return _radios.Append("all");
            if (radioSubcommand is "alert" or "check" or "events" or "watch" or "clear") return _radios;
        }
        if (command != "sds") return [];

        var subcommand = tokens[1].ToLowerInvariant();
        if (tokens.Count == 2) return _radios;
        if (subcommand == "send" && tokens.Count == 3) return ["tg", "fug", "service"];
        if (subcommand == "send" && tokens.Count == 4)
        {
            if (tokens[3].Equals("service", StringComparison.OrdinalIgnoreCase)) return ["990099"];
            if (tokens[3].Equals("tg", StringComparison.OrdinalIgnoreCase)) return _talkgroups;
        }
        return [];
    }

    private static IEnumerable<string> CompletePath(string prefix, bool quoted)
    {
        var separator = Math.Max(prefix.LastIndexOf('/'), prefix.LastIndexOf(Path.DirectorySeparatorChar));
        var directoryText = separator >= 0 ? prefix[..(separator + 1)] : string.Empty;
        var namePrefix = separator >= 0 ? prefix[(separator + 1)..] : prefix;
        var directory = directoryText.Length == 0 ? "." : directoryText;
        try
        {
            return Directory.EnumerateFileSystemEntries(directory)
                .Select(path => (Value: directoryText + Path.GetFileName(path), IsDirectory: Directory.Exists(path)))
                .Where(candidate => Path.GetFileName(candidate.Value)
                    .StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase))
                .Select(candidate => candidate.IsDirectory
                    ? FormatDirectory(candidate.Value, quoted)
                    : FormatFile(candidate.Value, quoted))
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    private static string FormatDirectory(string path, bool quoted)
    {
        var value = path + Path.DirectorySeparatorChar;
        return !quoted && value.Any(char.IsWhiteSpace) ? $"\"{value}" : value;
    }

    private static string FormatFile(string path, bool quoted)
    {
        if (quoted) return path + '"';
        return path.Any(char.IsWhiteSpace) ? $"\"{path}\"" : path;
    }

    private static int FindTokenStart(string line, int cursor, out bool quoted)
    {
        char quote = '\0';
        var tokenStart = 0;
        for (var index = 0; index < cursor; index++)
        {
            var character = line[index];
            if (quote != '\0')
            {
                if (character == quote) quote = '\0';
                continue;
            }
            if (character is '\'' or '"')
            {
                quote = character;
                tokenStart = index + 1;
            }
            else if (char.IsWhiteSpace(character)) tokenStart = index + 1;
        }
        quoted = quote != '\0';
        return tokenStart;
    }

    private static List<string> TokenizeLoose(string line)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        char quote = '\0';
        foreach (var character in line)
        {
            if (quote != '\0')
            {
                if (character == quote) quote = '\0';
                else current.Append(character);
            }
            else if (character is '\'' or '"') quote = character;
            else if (char.IsWhiteSpace(character))
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
            }
            else current.Append(character);
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    private static string ExpandCommand(string command) => command.ToLowerInvariant() switch
    {
        "l" => "login", "p" => "ping", "dc" => "disconnect", "s" => "send", "r" => "run",
        "rv" => "receive", "w" => "watch", "cls" => "clear", "st" => "status", _ => command.ToLowerInvariant()
    };
}
