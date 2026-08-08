using System.Buffers.Binary;
using System.Collections.Concurrent;
using DMRoute_ng.Core;
using DMRoute_ng.Registry;
using DMRoute_ng.Types;
using Microsoft.Extensions.Logging;

namespace DMRoute_ng.Routing;

public class MicroSubnetRouter
{
    private readonly ILogger<MicroSubnetRouter> _logger;
    private readonly RepeaterRegistry _registry;
    
    // Die feste Basis-Zone, für die dieser Master zuständig ist (z.B. 100)
    private readonly int _masterZoneId;

    // Lokales Status-Tracking (DMR-ID des Geräts → ID des lokalen Repeaters/Hotspots)
    private readonly ConcurrentDictionary<int, int> _localDeviceRouting = new();

    // Event für Phase 2 (SdsGateway)
    public event Action<byte[], string>? OnDataFrameReceived;

    public MicroSubnetRouter(ILogger<MicroSubnetRouter> logger, RepeaterRegistry registry, int masterZoneId)
    {
        _logger = logger;
        _registry = registry;
        _masterZoneId = masterZoneId;
    }

    // ReSharper disable once CognitiveComplexity
    public void RouteDmrd(ReadOnlySpan<byte> packet, IDmrSender sender)
    {
        if (packet.Length < 23) return; 

        // 1. IDs aus dem Homebrew-Header extrahieren
        var srcId = (packet[5] << 16) | (packet[6] << 8) | packet[7];
        var dstId = (packet[8] << 16) | (packet[9] << 8) | packet[10];
        var repeaterId = BinaryPrimitives.ReadInt32BigEndian(packet.Slice(11, 4));
        
        // 2. Flags aus Byte 15 auslesen
        var bits = packet[15];
        var isUnitCall = (bits & 0x40) != 0; 
        var isGroupCall = !isUnitCall;

        // Frame-Typ (Voice vs. Data) exakt auslesen.
        var isDataFrame = (packet[15] & 0x20) != 0; 
        
        if (!_registry.TryGet(repeaterId, out var sourceRepeater) || sourceRepeater == null) return;

        // 3. Status-Tracking: Welches Gerät sendet gerade über welchen Hotspot?
        _localDeviceRouting[srcId] = repeaterId;

        // 4. SdsGateway-Abfang (Phase 2)
        if (isDataFrame)
        {
            // _logger.LogDebug("Data-Frame von {SrcId} empfangen. Geht ans SdsGateway...", srcId);
            OnDataFrameReceived?.Invoke([.. packet], sourceRepeater.EndPoint!.ToString());
        }

        // 5. Voice-Routing (Phase 1)
        var routeCount = 0;

        if (isGroupCall)
        {
            // Lokales Group-Routing: An alle Hotspots in unserer Zone senden (außer zum Absender)
            foreach (var kvp in _registry.GetAll())
            {
                var peer = kvp.Value;
                if (peer.State != RepeaterState.LoggedIn || peer.Id == repeaterId) continue;
    
                sender.SendTo(packet, peer.EndPoint!);
                routeCount++;
            }
        }
        else if (isUnitCall)
        {
            // Subnetting: Gehört das Ziel zu unserer Zone?
            var targetZoneId = dstId / 100;

            if (targetZoneId == _masterZoneId)
            {
                // --- LOKALER PRIVATE CALL ---
                if (_localDeviceRouting.TryGetValue(dstId, out var targetRepeaterId))
                {
                    if (_registry.TryGet(targetRepeaterId, out var targetRepeater) && targetRepeater != null)
                    {
                        // Gezielter Unicast an exakt den Hotspot, wo das Gerät zuletzt war
                        sender.SendTo(packet, targetRepeater.EndPoint!);
                        routeCount++;
                    }
                }
                else
                {
                    _logger.LogWarning("Lokales Ziel {DstId} unbekannt. Das Gerät hat sich noch nicht gemeldet", dstId);
                    // Optional: Später könnte man hier ein Paging (Broadcast) senden, um das Gerät zu wecken.
                }
            }
            else
            {
                // --- FREMD-ZONE ---
                _logger.LogDebug("Ziel {DstId} gehört zu Fremd-Zone {TargetZone}. Paket wird (noch) verworfen", dstId, targetZoneId);
                // Hier greift in Phase 4 die M2M-Tabelle: Unicast an die WireGuard-IP des anderen Masters.
            }
        }

        if (routeCount > 0)
        {
            _logger.LogDebug("--> {Type} von {SrcId} an {DstId} geroutet (Ziele: {Count})", 
                isUnitCall ? "PrivateCall" : "GroupCall", srcId, dstId, routeCount);
        }
    }
}