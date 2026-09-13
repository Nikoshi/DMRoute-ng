using System.Net;

namespace DMRoute_ng.Emulator;

public sealed record VirtualHotspotOptions
{
    public required IPEndPoint MasterEndPoint { get; init; }
    public required int RepeaterId { get; init; }
    public required string PreSharedKey { get; init; }
    public HotspotConfiguration Configuration { get; init; } = new();
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan? KeepaliveInterval { get; init; }
    public int ReceiveQueueCapacity { get; init; } = 256;
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}
