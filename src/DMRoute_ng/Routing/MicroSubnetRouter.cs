using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using DMRoute_ng.Core;
using DMRoute_ng.Registry;
using DMRoute_ng.Types;
using Microsoft.Extensions.Logging;

namespace DMRoute_ng.Routing;

public class MicroSubnetRouter(
    ILogger<MicroSubnetRouter> logger,
    RepeaterRegistry registry,
    MasterRegistry masterRegistry,
    int masterZoneId)
{
    private readonly ConcurrentDictionary<int, int> _localDeviceRouting = new();

    public event Action<byte[], string>? OnDataFrameReceived;

    // MasterRegistry injizieren

    // ReSharper disable once CognitiveComplexity
    public void RouteDmrd(ReadOnlySpan<byte> packet, IPEndPoint remoteEndPoint, IDmrSender sender)
    {
        if (packet.Length < 23) return; 

        var srcId = (packet[5] << 16) | (packet[6] << 8) | packet[7];
        var dstId = (packet[8] << 16) | (packet[9] << 8) | packet[10];
        var repeaterId = BinaryPrimitives.ReadInt32BigEndian(packet.Slice(11, 4));
        
        var bits = packet[15];
        var isUnitCall = (bits & 0x40) != 0; 
        var isGroupCall = !isUnitCall;
        var isDataFrame = (packet[15] & 0x20) != 0; 
        
        // 1. Herkunft prüfen (Lokal vs. Mesh)
        var isLocalOrigin = registry.TryGet(repeaterId, out var sourceRepeater) && sourceRepeater.State == RepeaterState.LoggedIn;
        var isMeshOrigin = false;

        if (!isLocalOrigin)
        {
            var originZoneId = repeaterId / 10000;
            if (masterRegistry.TryGet(originZoneId, out var masterPeer) && masterPeer.DataEndPoint.Equals(remoteEndPoint))
            {
                isMeshOrigin = true;
            }
        }

        if (!isLocalOrigin && !isMeshOrigin) return;

        // 2. Lokales Status-Tracking (Nur für eigene Hotspots)
        if (isLocalOrigin)
        {
            _localDeviceRouting[srcId] = repeaterId;
        }

        if (isDataFrame)
        {
            var epString = isLocalOrigin ? sourceRepeater!.EndPoint!.ToString() : remoteEndPoint.ToString();
            OnDataFrameReceived?.Invoke([.. packet], epString);
        }

        var routeCount = 0;

        // 3. Routing-Weiche
        if (isGroupCall)
        {
            // A: An lokale Hotspots verteilen
            foreach (var kvp in registry.GetAll())
            {
                var peer = kvp.Value;
                if (peer.State != RepeaterState.LoggedIn || (isLocalOrigin && peer.Id == repeaterId)) continue;
    
                sender.SendTo(packet, peer.EndPoint!);
                routeCount++;
            }

            // B: An andere Master verteilen (Nur wenn Ursprung lokal ist -> verhindert Mesh-Routing-Loops)
            if (isLocalOrigin)
            {
                foreach (var kvp in masterRegistry.GetAll())
                {
                    sender.SendTo(packet, kvp.Value.DataEndPoint);
                    routeCount++;
                }
            }
        }
        else if (isUnitCall)
        {
            var targetZoneId = dstId / 100;

            if (targetZoneId == masterZoneId)
            {
                // Ziel ist unsere Zone (Lokaler Unicast)
                if (_localDeviceRouting.TryGetValue(dstId, out var targetRepeaterId) && 
                    registry.TryGet(targetRepeaterId, out var targetRepeater))
                {
                    sender.SendTo(packet, targetRepeater.EndPoint!);
                    routeCount++;
                }
                else
                {
                    logger.LogWarning("Lokales Ziel {DstId} unbekannt", dstId);
                }
            }
            else
            {
                // Ziel ist Fremd-Zone (Mesh Unicast)
                if (masterRegistry.TryGet(targetZoneId, out var targetMaster))
                {
                    sender.SendTo(packet, targetMaster.DataEndPoint);
                    logger.LogInformation("--> Mesh PrivateCall von {SrcId} an {DstId} weitergeleitet an Zone {Zone} ({Ip})", 
                        srcId, dstId, targetZoneId, targetMaster.DataEndPoint);
                    routeCount++;
                }
                else
                {
                    logger.LogDebug("Ziel {DstId} (Zone {TargetZone}) unbekannt oder offline", dstId, targetZoneId);
                }
            }
        }
    }
}