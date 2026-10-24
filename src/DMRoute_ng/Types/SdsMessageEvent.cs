namespace DMRoute_ng.Types;

public readonly struct SdsMessageEvent(
    int sourceId,
    int destinationId,
    int hotspotId,
    bool isGroupCall,
    byte encoding,
    bool containsLocation,
    long startedAtTicks,
    long completedAtTicks)
{
    public int SourceId { get; } = sourceId;
    public int DestinationId { get; } = destinationId;
    public int HotspotId { get; } = hotspotId;
    public bool IsGroupCall { get; } = isGroupCall;
    public byte Encoding { get; } = encoding;
    public bool ContainsLocation { get; } = containsLocation;
    public long StartedAtTicks { get; } = startedAtTicks;
    public long CompletedAtTicks { get; } = completedAtTicks;
    public long ReceiveDurationTicks => Math.Max(0, CompletedAtTicks - StartedAtTicks);
}
