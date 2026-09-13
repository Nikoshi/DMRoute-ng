namespace DMRoute_ng.Configuration;

internal interface ISetupTerminal
{
    bool IsInteractive { get; }
    void Clear();
    void Write(string text);
    ConsoleKeyInfo ReadKey();
    string? ReadValue(string prompt, string currentValue, bool secret);
}

internal sealed class ConsoleSetupTerminal : ISetupTerminal
{
    public bool IsInteractive => !Console.IsInputRedirected && !Console.IsOutputRedirected;

    public void Clear() => Console.Clear();

    public void Write(string text) => Console.Write(text);

    public ConsoleKeyInfo ReadKey() => Console.ReadKey(intercept: true);

    public string? ReadValue(string prompt, string currentValue, bool secret)
    {
        Console.Write(prompt);
        if (!secret)
        {
            Console.Write($" [{currentValue}]: ");
            return Console.ReadLine();
        }

        Console.Write(" [leave empty to keep current]: ");
        var buffer = new char[512];
        var length = 0;
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return new string(buffer, 0, length);
            }

            if (key.Key == ConsoleKey.Escape)
            {
                Console.WriteLine();
                return null;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (length == 0) continue;
                length--;
                Console.Write("\b \b");
                continue;
            }

            if (char.IsControl(key.KeyChar) || length == buffer.Length) continue;
            buffer[length++] = key.KeyChar;
            Console.Write('*');
        }
    }
}
