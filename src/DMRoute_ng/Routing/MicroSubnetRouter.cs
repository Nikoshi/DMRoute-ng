using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using DMRoute_ng.Core;
using DMRoute_ng.Registry;
using DMRoute_ng.Types;
using Microsoft.Extensions.Logging;

namespace DMRoute_ng.Routing;

public class MicroSubnetRouter
{
    private readonly ILogger<MicroSubnetRouter> _logger;
    private readonly RepeaterRegistry _registry;
    private readonly MasterRegistry _masterRegistry;
    private readonly int _masterZoneId;

    private readonly ConcurrentDictionary<int, int> _localDeviceRouting = new();
    
    // Tracking für aktive Anrufe (Zero-Allocation über ConcurrentDictionary)
    // Key: SourceId (DMR-ID des Senders)
    // Value: Letzter Zeitstempel in Ticks (für künftige Timeouts/Housekeeping)
    private readonly ConcurrentDictionary<int, long> _activeCalls = new();

    public event Action<byte[], string>? OnDataFrameReceived;
    
    // Neues Event für externe Status-Dienste (z. B. Dashboards oder Call-Logs)
    public event Action<int, int, bool, byte>? OnSignalingReceived;

    public MicroSubnetRouter(ILogger<MicroSubnetRouter> logger, RepeaterRegistry registry, MasterRegistry masterRegistry, int masterZoneId)
    {
        _logger = logger;
        _registry = registry;
        _masterRegistry = masterRegistry;
        _masterZoneId = masterZoneId;
    }

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
        
        // Neu: Datentyp extrahieren (unterste 4 Bits)
        var dataType = (byte)(bits & 0x0F);
        
        // 1. Herkunft prüfen (Lokal vs. Mesh)
        bool isLocalOrigin = _registry.TryGet(repeaterId, out var sourceRepeater) && sourceRepeater.State == RepeaterState.LoggedIn;
        bool isMeshOrigin = false;

        if (!isLocalOrigin)
        {
            var originZoneId = repeaterId / 10000;
            if (_masterRegistry.TryGet(originZoneId, out var masterPeer) && masterPeer.DataEndPoint.Equals(remoteEndPoint))
            {
                isMeshOrigin = true;
            }
        }

        if (!isLocalOrigin && !isMeshOrigin) return;

        // 2. Lokales Status-Tracking
        if (isLocalOrigin)
        {
            _localDeviceRouting[srcId] = repeaterId;
        }

        // 3. Call-State-Signalisierung und Tracking (Hot-Path)
        HandleSignaling(srcId, dstId, isGroupCall, dataType);

        if (isDataFrame)
        {
            string epString = isLocalOrigin ? sourceRepeater!.EndPoint!.ToString() : remoteEndPoint.ToString();
            OnDataFrameReceived?.Invoke([.. packet], epString);
        }

        var routeCount = 0;

        // 4. Routing-Weiche
        if (isGroupCall)
        {
            foreach (var kvp in _registry.GetAll())
            {
                var peer = kvp.Value;
                if (peer.State != RepeaterState.LoggedIn || (isLocalOrigin && peer.Id == repeaterId)) continue;
    
                sender.SendTo(packet, peer.EndPoint!);
                routeCount++;
            }

            if (isLocalOrigin)
            {
                foreach (var kvp in _masterRegistry.GetAll())
                {
                    sender.SendTo(packet, kvp.Value.DataEndPoint);
                    routeCount++;
                }
            }
        }
        else if (isUnitCall)
        {
            var targetZoneId = dstId / 100;

            if (targetZoneId == _masterZoneId)
            {
                if (_localDeviceRouting.TryGetValue(dstId, out var targetRepeaterId) && 
                    _registry.TryGet(targetRepeaterId, out var targetRepeater))
                {
                    sender.SendTo(packet, targetRepeater.EndPoint!);
                    routeCount++;
                }
                else
                {
                    // Fehler-Log nur noch bei Rufaufbau (0x01) oder CSBK (0x03) verhindern Spam
                    if (dataType == 0x01 || dataType == 0x03)
                    {
                        _logger.LogWarning("Lokales Ziel {DstId} unbekannt.", dstId);
                    }
                }
            }
            else
            {
                if (_masterRegistry.TryGet(targetZoneId, out var targetMaster))
                {
                    sender.SendTo(packet, targetMaster.DataEndPoint);
                    
                    // Erfolgs-Log ebenfalls nur noch einmalig bei Start
                    if (dataType == 0x01)
                    {
                        _logger.LogInformation("--> Mesh PrivateCall-Start von {SrcId} an {DstId} (Zone {Zone})", 
                            srcId, dstId, targetZoneId);
                    }
                    routeCount++;
                }
                else
                {
                    if (dataType == 0x01 || dataType == 0x03)
                    {
                        _logger.LogDebug("Ziel {DstId} (Zone {TargetZone}) unbekannt oder offline.", dstId, targetZoneId);
                    }
                }
            }
        }
    }

    private void HandleSignaling(int srcId, int dstId, bool isGroupCall, byte dataType)
    {
        if (dataType == 0x01) // Rufbeginn (Voice LC Header)
        {
            if (_activeCalls.TryAdd(srcId, DateTime.UtcNow.Ticks))
            {
                _logger.LogInformation("START: {CallType} von {SrcId} an {DstId}", isGroupCall ? "GroupCall" : "PrivateCall", srcId, dstId);
                OnSignalingReceived?.Invoke(srcId, dstId, isGroupCall, dataType);
            }
            else
            {
                _activeCalls[srcId] = DateTime.UtcNow.Ticks;
            }
        }
        else if (dataType == 0x02) // Rufende (Voice Terminator)
        {
            if (_activeCalls.TryRemove(srcId, out _))
            {
                _logger.LogInformation("ENDE:  {CallType} von {SrcId} an {DstId}", isGroupCall ? "GroupCall" : "PrivateCall", srcId, dstId);
                OnSignalingReceived?.Invoke(srcId, dstId, isGroupCall, dataType);
            }
        }
        else if (dataType == 0x03) // CSBK (Control Signalling Block - OACSU)
        {
            // CSBK wird beim PrivateCall oft vorab (als "Ping") gesendet, um das Ziel aufzuwecken.
            _logger.LogDebug("CSBK: Signalisierung von {SrcId} an {DstId}", srcId, dstId);
            OnSignalingReceived?.Invoke(srcId, dstId, isGroupCall, dataType);
        }
        else if (dataType == 0x00) // Laufendes Audio (Voice Frame ohne LC)
        {
            if (_activeCalls.ContainsKey(srcId))
            {
                _activeCalls[srcId] = DateTime.UtcNow.Ticks;
            }
        }
    }
}