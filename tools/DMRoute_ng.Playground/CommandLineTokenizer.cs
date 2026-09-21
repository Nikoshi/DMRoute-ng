using System.Text;

namespace DMRoute_ng.Playground;

internal static class CommandLineTokenizer
{
    public static bool TryTokenize(string line, out string[] tokens, out string? error)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        char quote = '\0';

        foreach (var character in line)
        {
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(character);
                }
                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                Flush(current, result);
                continue;
            }

            current.Append(character);
        }

        if (quote != '\0')
        {
            tokens = [];
            error = "Nicht abgeschlossene Zeichenkette.";
            return false;
        }

        Flush(current, result);
        tokens = result.ToArray();
        error = null;
        return true;
    }

    private static void Flush(StringBuilder current, List<string> result)
    {
        if (current.Length == 0) return;
        result.Add(current.ToString());
        current.Clear();
    }
}
