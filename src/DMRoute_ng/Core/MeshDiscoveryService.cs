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
    private readonly int _myZoneId;
    private readonly ushort _myDataPort;
    private readonly int _discoveryPort;
    private readonly byte[] _meshPskBytes;

    private readonly Socket _socket;

    public MeshDiscoveryService(ILogger<MeshDiscoveryService> logger, MasterRegistry masterRegistry, 
        int myZoneId, ushort myDataPort, int discoveryPort, string meshPsk)
    {
        _logger = logger;
        _masterRegistry = masterRegistry;
        _myZoneId = myZoneId;
        _myDataPort = myDataPort;
        _discoveryPort = discoveryPort;
        _meshPskBytes = Encoding.UTF8.GetBytes(meshPsk);

        // Native Sockets für maximale Performance und Span-Support
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
        _socket.Bind(new IPEndPoint(IPAddress.Any, _discoveryPort));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Mesh: Discovery Service für Zone {Zone} auf UDP {Port} gestartet", _myZoneId, _discoveryPort);

        // Starte den Broadcast-Sender als entkoppelten Task
        _ = Task.Run(() => BroadcastLoopAsync(stoppingToken), stoppingToken);

        byte[] buffer = new byte[1024];
        EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, remoteEP);
                ProcessBeacon(buffer.AsSpan(0, result.ReceivedBytes), (IPEndPoint)result.RemoteEndPoint);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Fehler beim Empfang eines Mesh-Beacons");
            }
        }
    }

    private async Task BroadcastLoopAsync(CancellationToken token)
    {
        // 50 Bytes Payload: [0-3: Magic] [4-7: ZoneId] [8-9: DataPort] [10-17: Ticks] [18-49: HMAC-SHA256]
        byte[] packet = new byte[50]; 
        var broadcastEndpoint = new IPEndPoint(IPAddress.Broadcast, _discoveryPort);

        Encoding.ASCII.GetBytes("DMBC").CopyTo(packet, 0);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(4, 4), _myZoneId);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(8, 2), _myDataPort);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(token))
        {
            // Setze aktuellen Zeitstempel (verhindert Replay-Attacken)
            BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(10, 8), DateTime.UtcNow.Ticks);

            // Generiere Signatur über die ersten 18 Bytes
            HMACSHA256.HashData(_meshPskBytes, packet.AsSpan(0, 18), packet.AsSpan(18, 32));

            await _socket.SendToAsync(packet, SocketFlags.None, broadcastEndpoint);
        }
    }

    private void ProcessBeacon(ReadOnlySpan<byte> payload, IPEndPoint remote)
    {
        if (payload.Length != 50) return; 
        if (!payload.Slice(0, 4).SequenceEqual("DMBC"u8)) return; 

        var remoteZone = BinaryPrimitives.ReadInt32BigEndian(payload.Slice(4, 4));
        var remoteDataPort = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(8, 2));
        var remoteTicks = BinaryPrimitives.ReadInt64BigEndian(payload.Slice(10, 8));
        
        // Eigenen Beacon ignorieren
        if (remoteZone == _myZoneId) return;

        // Anti-Replay / Stale Beacon Check (max 30 Sekunden Abweichung)
        var beaconTime = new DateTime(remoteTicks, DateTimeKind.Utc);
        if (Math.Abs((DateTime.UtcNow - beaconTime).TotalSeconds) > 30)
        {
            _logger.LogDebug("Mesh: Asynchroner/Veralteter Beacon von Zone {Zone} ({IP}) abgelehnt", remoteZone, remote.Address);
            return;
        }

        // Kryptografische Verifizierung der Daten via Stackalloc (0 Garbage)
        Span<byte> computedHash = stackalloc byte[32];
        HMACSHA256.HashData(_meshPskBytes, payload.Slice(0, 18), computedHash);

        if (!CryptographicOperations.FixedTimeEquals(computedHash, payload.Slice(18, 32)))
        {
            _logger.LogWarning("Mesh: Ungültige HMAC-Signatur von {IP}! Falscher PSK?", remote.Address);
            return;
        }
        
        var dataEndpoint = new IPEndPoint(remote.Address, remoteDataPort);
        
        // Eintragen / Aktualisieren in der Registry
        if (_masterRegistry.AddOrUpdate(remoteZone, dataEndpoint))
        {
            _logger.LogInformation("Mesh: [+] NEUER Master (Zone {Zone}) authentifiziert: {IP}:{Port}", 
                remoteZone, remote.Address, remoteDataPort);
        }
    }
}