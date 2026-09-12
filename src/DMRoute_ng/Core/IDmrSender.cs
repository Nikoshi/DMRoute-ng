using DMRoute_ng.Types;

namespace DMRoute_ng.Core;

public interface IDmrSender
{
    void SendTo(ReadOnlySpan<byte> packet, Ipv4Endpoint endPoint);
}
