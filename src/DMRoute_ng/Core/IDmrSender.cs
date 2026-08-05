using System.Net;

namespace DMRoute_ng.Core;

public interface IDmrSender
{
    void SendTo(ReadOnlySpan<byte> packet, IPEndPoint endPoint);
}