using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using DMRoute_ng.Registry;

namespace DMRoute_ng.Core;

public sealed class MeshDiscoveryService : BackgroundService
{
    private readonly ILogger<MeshDiscoveryService> _logger;
    private readonly MasterRegistry _masterRegistry;
    private readonly RoamingRegistry _roamingRegistry; // Neu
    private readonly int _myZoneId;
    private readonly ushort _myDataPort;
    private readonly int _discoveryPort;
    private readonly byte[] _meshPskBytes;

    private readonly Socket _socket;

    public MeshDiscoveryService(ILogger<MeshDiscoveryService> logger, MasterRegistry masterRegistry, RoamingRegistry roamingRegistry,
        int myZoneId, ushort myDataPort, int discoveryPort, string meshPsk)
    {
        _logger = logger;
        _masterRegistry = masterRegistry;
        _roamingRegistry = roamingRegistry;
        _myZoneId = myZoneId;
        _myDataPort = myDataPort;
        _discoveryPort = discoveryPort;
        _meshPskBytes = Encoding.UTF8.GetBytes(meshPsk);

        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
        _socket.Bind(new IPEndPoint(IPAddress.Any, _discoveryPort));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Mesh: Discovery Service für Zone {Zone} auf UDP {Port} gestartet", _myZoneId, _discoveryPort);

        _ = Task.Run(() => BroadcastLoopAsync(stoppingToken), stoppingToken);

        var buffer = new byte[1024];
        EndPoint remoteEp = new IPEndPoint(IPAddress.Any, 0);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, remoteEp);
                ProcessPacket(buffer.AsSpan(0, result.ReceivedBytes), (IPEndPoint)result.RemoteEndPoint);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Fehler beim Empfang eines Mesh-Pakets");
            }
        }
    }

    private async Task BroadcastLoopAsync(CancellationToken token)
    {
        var packet = new byte[50]; 
        var broadcastEndpoint = new IPEndPoint(IPAddress.Broadcast, _discoveryPort);

        "DMBC"u8.ToArray().CopyTo(packet, 0);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(4, 4), _myZoneId);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(8, 2), _myDataPort);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(token))
        {
            BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(10, 8), DateTime.UtcNow.Ticks);
            HMACSHA256.HashData(_meshPskBytes, packet.AsSpan(0, 18), packet.AsSpan(18, 32));
            await _socket.SendToAsync(packet, SocketFlags.None, broadcastEndpoint);
        }
    }

    public async Task SendLocationUpdateAsync(int deviceId, IPAddress targetAddress)
    {
        // Wir senden explizit an den Mesh-/Discovery-Port, nicht an den DMR-Port!
        var targetEndpoint = new IPEndPoint(targetAddress, _discoveryPort);

        var packet = new byte[52];
        "ROAM"u8.ToArray().CopyTo(packet, 0);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(4, 4), deviceId);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(8, 4), _myZoneId);
        BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(12, 8), DateTime.UtcNow.Ticks);

        HMACSHA256.HashData(_meshPskBytes, packet.AsSpan(0, 20), packet.AsSpan(20, 32));

        try
        {
            await _socket.SendToAsync(packet, SocketFlags.None, targetEndpoint);
            _logger.LogDebug("ROAM Update für {DeviceId} an {Endpoint} gesendet.", deviceId, targetEndpoint);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler beim Senden des ROAM-Updates");
        }
    }

    private void ProcessPacket(ReadOnlySpan<byte> payload, IPEndPoint remote)
    {
        if (payload.Length < 4) return; 

        if (payload[..4].SequenceEqual("DMBC"u8))
        {
            ProcessBeacon(payload, remote);
        }
        else if (payload[..4].SequenceEqual("ROAM"u8))
        {
            ProcessLocationUpdate(payload, remote);
        }
    }

    private void ProcessBeacon(ReadOnlySpan<byte> payload, IPEndPoint remote)
    {
        if (payload.Length != 50) return; 

        var remoteZone = BinaryPrimitives.ReadInt32BigEndian(payload.Slice(4, 4));
        var remoteDataPort = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(8, 2));
        var remoteTicks = BinaryPrimitives.ReadInt64BigEndian(payload.Slice(10, 8));
        
        if (remoteZone == _myZoneId) return;

        var beaconTime = new DateTime(remoteTicks, DateTimeKind.Utc);
        if (Math.Abs((DateTime.UtcNow - beaconTime).TotalSeconds) > 30) return;

        Span<byte> computedHash = stackalloc byte[32];
        HMACSHA256.HashData(_meshPskBytes, payload.Slice(0, 18), computedHash);

        if (!CryptographicOperations.FixedTimeEquals(computedHash, payload.Slice(18, 32))) return;
        
        var dataEndpoint = new IPEndPoint(remote.Address, remoteDataPort);
        
        if (_masterRegistry.AddOrUpdate(remoteZone, dataEndpoint))
        {
            _logger.LogInformation("Mesh: [+] NEUER Master (Zone {Zone}) authentifiziert: {IP}:{Port}", 
                remoteZone, remote.Address, remoteDataPort);
        }
    }

    private void ProcessLocationUpdate(ReadOnlySpan<byte> payload, IPEndPoint remote)
    {
        if (payload.Length != 52) return;

        var deviceId = BinaryPrimitives.ReadInt32BigEndian(payload.Slice(4, 4));
        var foreignZoneId = BinaryPrimitives.ReadInt32BigEndian(payload.Slice(8, 4));
        var ticks = BinaryPrimitives.ReadInt64BigEndian(payload.Slice(12, 8));

        var beaconTime = new DateTime(ticks, DateTimeKind.Utc);
        if (Math.Abs((DateTime.UtcNow - beaconTime).TotalSeconds) > 30) return;

        Span<byte> computedHash = stackalloc byte[32];
        HMACSHA256.HashData(_meshPskBytes, payload.Slice(0, 20), computedHash);

        if (!CryptographicOperations.FixedTimeEquals(computedHash, payload.Slice(20, 32)))
        {
            _logger.LogWarning("Mesh: Ungültige HMAC-Signatur bei ROAM-Paket von {IP}", remote.Address);
            return;
        }

        _roamingRegistry.UpdateDeviceLocation(deviceId, foreignZoneId);
    }
}