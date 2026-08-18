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
    private readonly struct CallState(int dstId, bool isGroupCall, long startTicks, long ticks, bool pendingTermination = false)
    {
        public readonly int DstId = dstId;
        public readonly bool IsGroupCall = isGroupCall;
        public readonly long StartTicks = startTicks; // Neu
        public readonly long Ticks = ticks;
        public readonly bool PendingTermination = pendingTermination;
    }
    
    private readonly ILogger<MicroSubnetRouter> _logger;
    private readonly RepeaterRegistry _registry;
    private readonly MasterRegistry _masterRegistry;
    private readonly RoamingRegistry _roamingRegistry; 
    private readonly MeshDiscoveryService _meshService; 
    private readonly int _masterZoneId;

    private readonly ConcurrentDictionary<int, int> _localDeviceRouting = new();
    private readonly ConcurrentDictionary<int, CallState> _activeCalls = new();
    private readonly Timer _cleanupTimer;

    public event Action<byte[], string>? OnDataFrameReceived;
    public event Action<int, int, bool, byte, int>? OnSignalingReceived;
    public event Action<byte[], int, byte>? OnUnknownFrameReceived;
    public event Action<int, byte[]>? OnAprsReceived;

    public MicroSubnetRouter(
        ILogger<MicroSubnetRouter> logger, 
        RepeaterRegistry registry, 
        MasterRegistry masterRegistry, 
        RoamingRegistry roamingRegistry, 
        MeshDiscoveryService meshService, 
        int masterZoneId)
    {
        _logger = logger;
        _registry = registry;
        _masterRegistry = masterRegistry;
        _roamingRegistry = roamingRegistry;
        _meshService = meshService;
        _masterZoneId = masterZoneId;
        _cleanupTimer = new Timer(CleanupStaleCalls, null, 2000, 2000);
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
        var dataType = (byte)(bits & 0x0F);
        
        var isLocalOrigin = _registry.TryGet(repeaterId, out var sourceRepeater) && sourceRepeater.State == RepeaterState.LoggedIn;
        
        if (isLocalOrigin)
        {
            Volatile.Write(ref sourceRepeater!.LastPingTicks, DateTime.UtcNow.Ticks);
        }
        
        var isMeshOrigin = false;

        if (!isLocalOrigin)
        {
            var originZoneId = repeaterId / 10000;
            if (_masterRegistry.TryGet(originZoneId, out var masterPeer) && masterPeer.DataEndPoint.Equals(remoteEndPoint))
            {
                isMeshOrigin = true;
            }
        }

        if (!isLocalOrigin && !isMeshOrigin) return;

        // Herkunft verarbeiten & Roaming triggern
        if (isLocalOrigin)
        {
            var sourceHomeZone = srcId / 100;
            
            if (sourceHomeZone == _masterZoneId)
            {
                _localDeviceRouting[srcId] = repeaterId;
            }
            else
            {
                _roamingRegistry.TrackLocalGuest(srcId, sourceRepeater!.EndPoint!);
                _logger.LogInformation("DEBUG: Gast erkannt! SrcId: {SrcId}, Berechnete Home-Zone: {Zone}, DataType: {Type}", srcId, sourceHomeZone, dataType);

                if (dataType == 0x01 || dataType == 0x03)
                {
                    if (_masterRegistry.TryGet(sourceHomeZone, out var homeMaster))
                    {
                        _ = _meshService.SendLocationUpdateAsync(srcId, homeMaster.DataEndPoint.Address);
                    }
                }
            }
        }

        HandleSignaling(packet, srcId, dstId, isGroupCall, dataType);

        if (isDataFrame)
        {
            string epString = isLocalOrigin ? sourceRepeater!.EndPoint!.ToString() : remoteEndPoint.ToString();
            OnDataFrameReceived?.Invoke([.. packet], epString);
        }

        // Routing-Weiche
        if (isGroupCall)
        {
            foreach (var kvp in _registry.GetAll())
            {
                var peer = kvp.Value;
                if (peer.State != RepeaterState.LoggedIn || (isLocalOrigin && peer.Id == repeaterId)) continue;
                sender.SendTo(packet, peer.EndPoint!);
            }

            if (isLocalOrigin)
            {
                if (dstId == 1) 
                {
                    foreach (var kvp in _masterRegistry.GetAll()) { sender.SendTo(packet, kvp.Value.DataEndPoint); }
                }
                else if (dstId == 2) 
                {
                    // Kein Mesh-Routing
                }
                else if (dstId is >= 100 and <= 999) 
                {
                    if (_masterRegistry.TryGet(dstId, out var targetMaster)) { sender.SendTo(packet, targetMaster.DataEndPoint); }
                }
            }
        }
        else if (isUnitCall)
        {
            var targetHomeZone = dstId / 100;

            if (targetHomeZone == _masterZoneId)
            {
                if (_localDeviceRouting.TryGetValue(dstId, out var targetRepeaterId) && 
                    _registry.TryGet(targetRepeaterId, out var targetRepeater))
                {
                    // Ziel ist regulär daheim
                    sender.SendTo(packet, targetRepeater.EndPoint!);
                }
                else if (_roamingRegistry.TryGetRoamedDeviceZone(dstId, out int foreignZoneId))
                {
                    // Ziel roamt in fremder Zone
                    if (_masterRegistry.TryGet(foreignZoneId, out var foreignMaster))
                    {
                        sender.SendTo(packet, foreignMaster.DataEndPoint);
                    }
                }
                else
                {
                    if (dataType == 0x01 || dataType == 0x03)
                    {
                        _logger.LogWarning("Lokales Ziel {DstId} unbekannt und kein Roaming-Eintrag", dstId);
                    }
                }
            }
            else
            {
                if (_roamingRegistry.TryGetLocalGuestEndpoint(dstId, out var guestEndpoint))
                {
                    // Ziel ist als Gast am eigenen System
                    sender.SendTo(packet, guestEndpoint!);
                }
                else if (_masterRegistry.TryGet(targetHomeZone, out var targetMaster))
                {
                    // Ziel nicht bekannt, Paket an den Home-Master der Ziel-ID senden
                    sender.SendTo(packet, targetMaster.DataEndPoint);
                    
                    if (dataType == 0x01)
                    {
                        _logger.LogInformation("--> Mesh PrivateCall-Start von {SrcId} an {DstId} (Zone {Zone})", srcId, dstId, targetHomeZone);
                    }
                }
                else
                {
                    if (dataType == 0x01 || dataType == 0x03)
                    {
                        _logger.LogDebug("Ziel {DstId} (Zone {TargetZone}) unbekannt oder offline", dstId, targetHomeZone);
                    }
                }
            }
        }
    }

    private void HandleSignaling(ReadOnlySpan<byte> packet, int srcId, int dstId, bool isGroupCall, byte dataType)
    {
        long now = DateTime.UtcNow.Ticks;
        switch (dataType)
        {
            case 0x01:
                if (_activeCalls.TryGetValue(srcId, out var existingCall))
                {
                    _activeCalls[srcId] = new CallState(dstId, isGroupCall, existingCall.StartTicks, now, pendingTermination: false);
                }
                else
                {
                    if (_activeCalls.TryAdd(srcId, new CallState(dstId, isGroupCall, now, now, pendingTermination: false)))
                    {
                        _logger.LogInformation("START: {CallType} von {SrcId} an {DstId}", isGroupCall ? "GroupCall" : "PrivateCall", srcId, dstId);
                        OnSignalingReceived?.Invoke(srcId, dstId, isGroupCall, dataType, 0);
                    }
                }
                break;
            case 0x02:
                if (_activeCalls.TryGetValue(srcId, out var active))
                {
                    _activeCalls[srcId] = new CallState(active.DstId, active.IsGroupCall, active.StartTicks, now, pendingTermination: true);
                }
                break;
            case 0x03:
                if (dstId == 990099)
                {
                    _logger.LogInformation("APRS CSBK-Positionsdaten von {SrcId} empfangen", srcId);
                    // Raw Packet übergeben, Parsing erfolgt später
                    OnAprsReceived?.Invoke(srcId, [.. packet]);
                }
                else
                {
                    _logger.LogDebug("CSBK: Signalisierung von {SrcId} an {DstId}", srcId, dstId);
                    OnSignalingReceived?.Invoke(srcId, dstId, isGroupCall, dataType, 0);
                }
                break;
            case 0x00:
            case 0x04:
            case 0x05:
            case 0x06:
            case 0x07:
            case 0x08:
                if (_activeCalls.TryGetValue(srcId, out var current))
                {
                    _activeCalls[srcId] = new CallState(current.DstId, current.IsGroupCall, current.StartTicks, now, pendingTermination: false);
                }
                break;
            default:
                OnUnknownFrameReceived?.Invoke([.. packet], srcId, dataType);
                break;
        }
    }
    
    private void CleanupStaleCalls(object? state)
    {
        long currentTicks = DateTime.UtcNow.Ticks;
        long timeoutTicks = TimeSpan.FromSeconds(3).Ticks;
        long hangtimeTicks = TimeSpan.FromMilliseconds(1500).Ticks;

        foreach (var kvp in _activeCalls)
        {
            var call = kvp.Value;
            var elapsed = currentTicks - call.Ticks;

            if (call.PendingTermination && elapsed > hangtimeTicks)
            {
                if (_activeCalls.TryRemove(kvp.Key, out var removed))
                {
                    var durationSec = (int)((currentTicks - removed.StartTicks) / TimeSpan.TicksPerSecond);
                    _logger.LogInformation("ENDE:  {CallType} von {SrcId} an {DstId} (sauber beendet)", removed.IsGroupCall ? "GroupCall" : "PrivateCall", kvp.Key, removed.DstId);
                    OnSignalingReceived?.Invoke(kvp.Key, removed.DstId, removed.IsGroupCall, 0x02, durationSec);
                }
            }
            else if (!call.PendingTermination && elapsed > timeoutTicks)
            {
                if (_activeCalls.TryRemove(kvp.Key, out var removed))
                {
                    var durationSec = (int)((currentTicks - removed.StartTicks) / TimeSpan.TicksPerSecond);
                    _logger.LogWarning("TIMEOUT: Call von {SrcId} an {DstId} wegen Inaktivität abgebrochen", kvp.Key, removed.DstId);
                    OnSignalingReceived?.Invoke(kvp.Key, removed.DstId, removed.IsGroupCall, 0xFE, durationSec);
                }
            }
        }
    }
}