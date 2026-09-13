namespace DMRoute_ng.Emulator;

public class HomebrewProtocolException(string message, Exception? innerException = null)
    : Exception(message, innerException)
{
}

public sealed class HomebrewRejectedException(int repeaterId)
    : HomebrewProtocolException($"The master rejected repeater {repeaterId} with MSTNAK.")
{
    public int RepeaterId { get; } = repeaterId;
}

public sealed class HomebrewTimeoutException(string operation, TimeSpan timeout)
    : HomebrewProtocolException($"Timed out after {timeout} while waiting for {operation}.")
{
    public string Operation { get; } = operation;
    public TimeSpan Timeout { get; } = timeout;
}
