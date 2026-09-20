using DMRoute_ng.Types;

namespace DMRoute_ng.Services;

public interface ISdsService
{
    int DestinationId { get; }
    void HandleMessage(in SdsMessageEvent message, ReadOnlySpan<byte> textBytes);
}
