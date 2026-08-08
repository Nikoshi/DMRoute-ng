using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using DMRoute_ng.Registry;
using DMRoute_ng.Utils;

namespace DMRoute_ng.Core;

public sealed class MeshDiscoveryService : BackgroundService
{
    private readonly ILogger<MeshDiscoveryService> _logger;
    private readonly MasterRegistry _masterRegistry;
    private readonly int _myZoneId;
    private readonly ushort _myDataPort;
    private readonly int _discoveryPort;
    private readonly byte[] _meshPskBytes;

    private readonly UdpClient _udpClient;
    private readonly byte[] _currentNonce = new byte[32]; // Hält den Nonce für Validierungen

    public MeshDiscoveryService(ILogger<MeshDiscoveryService> logger, MasterRegistry masterRegistry, 
        int myZoneId, ushort myDataPort, int discoveryPort, string meshPsk)
    {
        _logger = logger;
        _masterRegistry = masterRegistry;
        _myZoneId = myZoneId;
        _myDataPort = myDataPort;
        _discoveryPort = discoveryPort;
        _meshPskBytes = Encoding.UTF8.GetBytes(meshPsk);

        _udpClient = new UdpClient();
        _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, _discoveryPort));
        _udpClient.EnableBroadcast = true;
        
        logger.LogInformation("DMRoute_ng Mesh Server lauscht auf UDP Port {Port}", discoveryPort);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Broadcast-Sender Task starten
        _ = Task.Run(() => SendBroadcastLoop(stoppingToken), stoppingToken);

        // Empfangs-Schleife
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await _udpClient.ReceiveAsync(stoppingToken);
                HandlePacket(result.Buffer.AsSpan(), result.RemoteEndPoint);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Fehler im Mesh-Discovery"); }
        }
    }

    private async Task SendBroadcastLoop(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        // Statt IPAddress.Broadcast (255.255.255.255) das spezifische Subnetz nutzen // TODO: Das muss dann natürlich entweder automatisch ermittelt werden oder beim Start ;)
        var broadcastEndpoint = new IPEndPoint(IPAddress.Parse("10.229.157.255"), _discoveryPort);

        // Statischer Puffer für DMBD [Header(4) + Zone(4) + Port(2) + Nonce(32)]
        var packet = new byte[42]; 
        PacketUtils.DmbdHeader.CopyTo(packet);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(4, 4), _myZoneId);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(8, 2), _myDataPort);

        while (await timer.WaitForNextTickAsync(token))
        {
            // Neuen Nonce generieren und in Puffer und lokalen State schreiben
            RandomNumberGenerator.Fill(_currentNonce);
            _currentNonce.CopyTo(packet.AsSpan(10, 32));

            await _udpClient.SendAsync(packet, broadcastEndpoint, token);
        }
    }

    private void HandlePacket(ReadOnlySpan<byte> payload, IPEndPoint remote)
    {
        if (payload.Length < 42) return;

        if (payload.StartsWith(PacketUtils.DmbdHeader))
        {
            HandleBroadcast(payload, remote);
        }
        else if (payload.StartsWith(PacketUtils.DmbcHeader))
        {
            HandleChallengeResponse(payload, remote);
        }
    }

    private void HandleBroadcast(ReadOnlySpan<byte> payload, IPEndPoint remote)
    {
        var remoteZoneId = BinaryPrimitives.ReadInt32BigEndian(payload.Slice(4, 4));
        if (remoteZoneId == _myZoneId) return; // Eigener Broadcast

        var remoteNonce = payload.Slice(10, 32);

        // Antwortpaket auf Stack allozieren (DMBC)
        Span<byte> response = stackalloc byte[42];
        PacketUtils.DmbcHeader.CopyTo(response);
        BinaryPrimitives.WriteInt32BigEndian(response.Slice(4, 4), _myZoneId);
        BinaryPrimitives.WriteUInt16BigEndian(response.Slice(8, 2), _myDataPort);

        // HMAC-SHA256 über empfangenen Nonce mit PSK als Schlüssel (Zero-Allocation)
        HMACSHA256.HashData(_meshPskBytes, remoteNonce, response.Slice(10, 32));

        // Unicast-Antwort an den Absender
        _udpClient.Client.SendTo(response, SocketFlags.None, remote);
    }

    private void HandleChallengeResponse(ReadOnlySpan<byte> payload, IPEndPoint remote)
    {
        var remoteZoneId = BinaryPrimitives.ReadInt32BigEndian(payload.Slice(4, 4));
        var remoteDataPort = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(8, 2));
        var receivedHash = payload.Slice(10, 32);

        // Erwarteten Hash über lokalen Nonce bilden
        Span<byte> expectedHash = stackalloc byte[32];
        HMACSHA256.HashData(_meshPskBytes, _currentNonce, expectedHash);

        if (CryptographicOperations.FixedTimeEquals(expectedHash, receivedHash))
        {
            // Verifiziert -> In Registry aufnehmen. Die Data-IP ist die IP des Absenders, Port kommt aus Payload.
            var dataEndpoint = new IPEndPoint(remote.Address, remoteDataPort);
            _masterRegistry.AddOrUpdate(remoteZoneId, dataEndpoint);
            
            _logger.LogInformation("Mesh: Zone {Zone} verifiziert unter {IP}:{Port}", remoteZoneId, remote.Address, remoteDataPort);
        }
    }
}