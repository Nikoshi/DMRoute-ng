using System.Text;

namespace DMRoute_ng.Playground;

internal sealed class PlaygroundCommandHistory(int capacity = 100)
{
    private readonly List<string> _entries = new(capacity);
    private int _index;
    private string _draft = string.Empty;

    public IReadOnlyList<string> Entries => _entries;

    public void Add(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        if (_entries.Count == 0 || !string.Equals(_entries[^1], line, StringComparison.Ordinal))
        {
            if (_entries.Count == capacity) _entries.RemoveAt(0);
            _entries.Add(line);
        }
        ResetNavigation();
    }

    public string Previous(string current)
    {
        if (_entries.Count == 0) return current;
        if (_index == _entries.Count) _draft = current;
        if (_index > 0) _index--;
        return _entries[_index];
    }

    public string Next(string current)
    {
        if (_entries.Count == 0 || _index >= _entries.Count) return current;
        _index++;
        return _index == _entries.Count ? _draft : _entries[_index];
    }

    public int FindPrevious(string query, int beforeIndex)
    {
        for (var index = Math.Min(beforeIndex - 1, _entries.Count - 1); index >= 0; index--)
            if (_entries[index].Contains(query, StringComparison.OrdinalIgnoreCase)) return index;
        return -1;
    }

    public void ResetNavigation()
    {
        _index = _entries.Count;
        _draft = string.Empty;
    }
}

internal sealed class PlaygroundConsoleLineEditor(PlaygroundCommandCompleter completer)
{
    private readonly PlaygroundCommandHistory _history = new();

    public string? ReadLine(string prompt, CancellationToken cancellationToken)
    {
        var buffer = new StringBuilder();
        var cursor = 0;
        var renderedLength = 0;
        Render(prompt, buffer.ToString(), cursor, ref renderedLength);

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!Console.KeyAvailable)
            {
                Thread.Sleep(20);
                continue;
            }

            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                var line = buffer.ToString();
                _history.Add(line);
                return line;
            }
            if (key.Key == ConsoleKey.UpArrow)
            {
                ReplaceBuffer(buffer, _history.Previous(buffer.ToString()), ref cursor);
            }
            else if (key.Key == ConsoleKey.DownArrow)
            {
                ReplaceBuffer(buffer, _history.Next(buffer.ToString()), ref cursor);
            }
            else if (key.Key == ConsoleKey.LeftArrow && cursor > 0) cursor--;
            else if (key.Key == ConsoleKey.RightArrow && cursor < buffer.Length) cursor++;
            else if (key.Key == ConsoleKey.Home) cursor = 0;
            else if (key.Key == ConsoleKey.End) cursor = buffer.Length;
            else if (key.Key == ConsoleKey.Backspace && cursor > 0)
            {
                buffer.Remove(--cursor, 1);
                _history.ResetNavigation();
            }
            else if (key.Key == ConsoleKey.Delete && cursor < buffer.Length)
            {
                buffer.Remove(cursor, 1);
                _history.ResetNavigation();
            }
            else if (key.Key == ConsoleKey.Tab)
            {
                Complete(prompt, buffer, ref cursor, ref renderedLength);
            }
            else if (key.Key == ConsoleKey.R && key.Modifiers.HasFlag(ConsoleModifiers.Control))
            {
                var selected = ReverseSearch(buffer.ToString(), cancellationToken, ref renderedLength);
                if (selected is not null) ReplaceBuffer(buffer, selected, ref cursor);
            }
            else if (!char.IsControl(key.KeyChar))
            {
                buffer.Insert(cursor++, key.KeyChar);
                _history.ResetNavigation();
            }
            Render(prompt, buffer.ToString(), cursor, ref renderedLength);
        }

        Console.WriteLine();
        return null;
    }

    private void Complete(string prompt, StringBuilder buffer, ref int cursor, ref int renderedLength)
    {
        var completion = completer.Complete(buffer.ToString(), cursor);
        if (completion.Candidates.Length == 0) return;
        var replacement = completion.Candidates.Length == 1
            ? completion.Candidates[0]
            : LongestCommonPrefix(completion.Candidates);
        var current = buffer.ToString(completion.Start, completion.Length);
        if (replacement.Length > current.Length)
        {
            buffer.Remove(completion.Start, completion.Length);
            buffer.Insert(completion.Start, replacement);
            cursor = completion.Start + replacement.Length;
            if (completion.Candidates.Length == 1 && cursor == buffer.Length &&
                (buffer.Length == 0 || !char.IsWhiteSpace(buffer[^1])))
            {
                buffer.Append(' ');
                cursor++;
            }
            _history.ResetNavigation();
            return;
        }

        Console.WriteLine();
        Console.WriteLine(string.Join("  ", completion.Candidates));
        renderedLength = 0;
        Render(prompt, buffer.ToString(), cursor, ref renderedLength);
    }

    private string? ReverseSearch(string original, CancellationToken cancellationToken, ref int renderedLength)
    {
        var query = new StringBuilder();
        var before = _history.Entries.Count;
        var matchIndex = -1;
        var match = string.Empty;
        while (!cancellationToken.IsCancellationRequested)
        {
            RenderSearch(query.ToString(), match, ref renderedLength);
            if (!Console.KeyAvailable) { Thread.Sleep(20); continue; }
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Escape) return original;
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return matchIndex >= 0 ? match : original;
            }
            if (key.Key == ConsoleKey.R && key.Modifiers.HasFlag(ConsoleModifiers.Control))
            {
                before = matchIndex >= 0 ? matchIndex : _history.Entries.Count;
            }
            else if (key.Key == ConsoleKey.Backspace && query.Length > 0)
            {
                query.Length--;
                before = _history.Entries.Count;
            }
            else if (!char.IsControl(key.KeyChar))
            {
                query.Append(key.KeyChar);
                before = _history.Entries.Count;
            }
            else continue;

            matchIndex = _history.FindPrevious(query.ToString(), before);
            match = matchIndex >= 0 ? _history.Entries[matchIndex] : string.Empty;
        }
        return null;
    }

    private static void Render(string prompt, string text, int cursor, ref int renderedLength)
    {
        var display = prompt + text;
        ClearLine(renderedLength);
        Console.Write(display);
        var tail = text.Length - cursor;
        if (tail > 0) Console.Write($"\u001b[{tail}D");
        renderedLength = display.Length;
    }

    private static void RenderSearch(string query, string match, ref int renderedLength)
    {
        var display = $"(reverse-i-search)`{query}': {match}";
        ClearLine(renderedLength);
        Console.Write(display);
        renderedLength = display.Length;
    }

    private static void ClearLine(int renderedLength)
    {
        Console.Write('\r');
        if (renderedLength > 0) Console.Write(new string(' ', renderedLength));
        Console.Write('\r');
    }

    private static void ReplaceBuffer(StringBuilder buffer, string value, ref int cursor)
    {
        buffer.Clear();
        buffer.Append(value);
        cursor = buffer.Length;
    }

    private static string LongestCommonPrefix(IReadOnlyList<string> values)
    {
        if (values.Count == 0) return string.Empty;
        var length = values[0].Length;
        for (var index = 1; index < values.Count; index++)
        {
            length = Math.Min(length, values[index].Length);
            var offset = 0;
            while (offset < length && char.ToUpperInvariant(values[0][offset]) == char.ToUpperInvariant(values[index][offset]))
                offset++;
            length = offset;
        }
        return values[0][..length];
    }
}
