using System.Buffers.Binary;
using DMRoute_ng.Core;
using DMRoute_ng.Registry;
using Microsoft.Extensions.Logging;

namespace DMRoute_ng.Routing;

public class MicroSubnetRouter(ILogger<MicroSubnetRouter> logger, RepeaterRegistry registry)
{
    private const int GlobalTalkgroup = 1;
    private const int LocalZoneTalkgroup = 10;

    public void RouteDmrd(ReadOnlySpan<byte> packet, IDmrSender sender)
    {
        if (packet.Length < 23) return; 

        var dstId = (packet[8] << 16) | (packet[9] << 8) | packet[10];
        var repeaterId = BinaryPrimitives.ReadInt32BigEndian(packet.Slice(11, 4));
        
        // Call-Typ aus den Bits extrahieren (Byte 15)
        var bits = packet[15];
        var isUnitCall = (bits & 0x40) != 0; // Einzelruf (Private Call)
        var isGroupCall = !isUnitCall;
        
        if (!registry.TryGet(repeaterId, out var sourceRepeater) || sourceRepeater == null) return;

        var senderZoneId = repeaterId / 100;
        var targetZoneId = -1;
        var isGlobal = false;

        if (isUnitCall)
        {
            // Private Call: Ziel-ID ist ein Gerät (z.B. 10025 -> Zone 100)
            targetZoneId = dstId / 100;
        }
        else if (isGroupCall)
        {
            switch (dstId)
            {
                case GlobalTalkgroup:
                    isGlobal = true; // Geht an alle
                    break;
                case LocalZoneTalkgroup:
                    targetZoneId = senderZoneId; // Lokale Zone
                    break;
                default:
                    targetZoneId = dstId; // Explizite Fremdzone (z.B. TG 100)
                    break;
            }
        }

        logger.LogDebug("Received {CallType} from {Repeater} to {DstId} ({TargetZone})", isUnitCall ? "Private Call": "Group Call",  repeaterId, dstId, targetZoneId);
        
        var routeCount = 0;

        foreach (var peer in registry.GetActivePeers())
        {
            if (peer.Id == repeaterId) continue;

            var peerZoneId = peer.Id / 100;

            if (isGlobal || peerZoneId == targetZoneId)
            {
                sender.SendTo(packet, peer.EndPoint!);
                routeCount++;
            }
        }

        if (routeCount > 0)
        {
            logger.LogDebug("--> DMRD geroutet (Group: {IsGroup}, Global: {IsGlobal}, TargetZone: {Zone}, Count: {Count})", 
                isGroupCall, isGlobal, targetZoneId, routeCount);
        }
    }
}