namespace DMRoute_ng.Playground;

internal interface IPlaygroundSetupTerminal
{
    bool IsInteractive { get; }
    void Write(string text);
    string? ReadLine();
    string? ReadSecret(string prompt);
}

internal sealed class ConsolePlaygroundSetupTerminal : IPlaygroundSetupTerminal
{
    public bool IsInteractive => !Console.IsInputRedirected && !Console.IsOutputRedirected;
    public void Write(string text) => Console.Write(text);
    public string? ReadLine() => Console.ReadLine();

    public string? ReadSecret(string prompt)
    {
        Console.Write(prompt);
        var value = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return new string(value.ToArray());
            }
            if (key.Key == ConsoleKey.Escape)
            {
                Console.WriteLine();
                return null;
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (value.Count == 0) continue;
                value.RemoveAt(value.Count - 1);
                Console.Write("\b \b");
                continue;
            }
            if (char.IsControl(key.KeyChar)) continue;
            value.Add(key.KeyChar);
            Console.Write('*');
        }
    }
}
