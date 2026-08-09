using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using DMRoute_ng.Registry;
using DMRoute_ng.Routing;
using DMRoute_ng.Types;
using DMRoute_ng.Utils;

namespace DMRoute_ng.Core;

public class DmrServer(ILogger<DmrServer> logger, RepeaterRegistry registry, MicroSubnetRouter router) 
    : BackgroundService, IDmrSender
{
    private const int DmrPort = 62031;
    private UdpClient? _udpClient;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _udpClient = new UdpClient(DmrPort);
        logger.LogInformation("DMRoute_ng Server lauscht auf UDP Port {Port}", DmrPort);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await _udpClient.ReceiveAsync(stoppingToken);
                var payload = result.Buffer.AsSpan();

                HandlePacket(payload, result.RemoteEndPoint);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Fehler beim Verarbeiten des UDP-Pakets");
            }
        }
        
        _udpClient.Dispose();
    }

    private void HandlePacket(ReadOnlySpan<byte> payload, IPEndPoint remoteEndPoint)
    {
        if (payload.Length < 4) return;

        if (payload.StartsWith(PacketUtils.RptlHeader))
        {
            HandleRptl(payload, remoteEndPoint);
        }
        else if (payload.StartsWith(PacketUtils.RptkHeader))
        {
            HandleRptk(payload);
        }
        else if (payload.StartsWith(PacketUtils.RptPingHeader))
        {
            HandleRptPing(payload, remoteEndPoint);
        }
        else if (payload.StartsWith(PacketUtils.DmrdHeader))
        {
            // remoteEndPoint übergeben
            router.RouteDmrd(payload, remoteEndPoint, this); 
        }
        else if (payload.StartsWith(PacketUtils.RptcHeader))
        {
            HandleRptc(payload, remoteEndPoint);
        }
        else if (payload.StartsWith(PacketUtils.DmrcHeader))
        {
            if (payload.Length >= 8)
            {
                var repId = BinaryPrimitives.ReadInt32BigEndian(payload.Slice(4, 4));
                logger.LogInformation("<-- DMRC (Hotspot Config Update) von ID {RepeaterId}", repId);
                
                // Hotspot beruhigen, indem wir die Konfiguration mit einem ACK abwinken
                SendTo(PacketUtils.BuildRptAck((uint)repId), remoteEndPoint);
            }
        }
    }

    private void HandleRptl(ReadOnlySpan<byte> payload, IPEndPoint endPoint)
    {
        if (payload.Length < 8) return;
        var repeaterId = BinaryPrimitives.ReadInt32BigEndian(payload[4..8]);

        //logger.LogInformation("<-- RPTL von ID {RepeaterId}", repeaterId);

        if (!registry.TryGet(repeaterId, out var repeater))
        {
            logger.LogWarning("Repeater {RepeaterId} ist nicht registriert (Whitelist)", repeaterId);
            SendTo(PacketUtils.BuildMstNak(repeaterId), endPoint);
            return;
        }

        var randomSalt = (uint)Random.Shared.Next();
        repeater.RandomNumber = randomSalt;
        repeater.EndPoint = endPoint;
        repeater.State = RepeaterState.ChallengeSent;

        //logger.LogInformation("--> RPTACK (Challenge gesendet an {RepeaterId})", repeaterId);
        SendTo(PacketUtils.BuildRptAck(randomSalt), endPoint);
    }

    private void HandleRptk(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 40) return; 

        var repeaterId = BinaryPrimitives.ReadInt32BigEndian(payload[4..8]);
        var receivedHash = payload[8..40];

        if (!registry.TryGet(repeaterId, out var repeater) || repeater == null) return;

        //logger.LogInformation("<-- RPTK von ID {RepeaterId}", repeaterId);

        var pskLength = Encoding.ASCII.GetByteCount(repeater.PreSharedKey);
        Span<byte> dataToHash = stackalloc byte[4 + pskLength];
        
        BinaryPrimitives.WriteUInt32BigEndian(dataToHash[..4], repeater.RandomNumber);
        
        if (pskLength > 0)
        {
            Encoding.ASCII.GetBytes(repeater.PreSharedKey, dataToHash[4..]);
        }

        Span<byte> calculatedHash = stackalloc byte[32];
        SHA256.HashData(dataToHash, calculatedHash);

        if (CryptographicOperations.FixedTimeEquals(calculatedHash, receivedHash))
        {
            repeater.State = RepeaterState.LoggedIn;
            Volatile.Write(ref repeater.LastPingTicks, DateTime.UtcNow.Ticks);

            //logger.LogInformation("--> RPTACK (Repeater {RepeaterId} erfolgreich eingeloggt)", repeaterId);
            SendTo(PacketUtils.BuildRptAck((uint)repeaterId), repeater.EndPoint!);
        }
        else
        {
            logger.LogWarning("--> MSTNAK (Hashes stimmen nicht überein für {RepeaterId}. Falsches Passwort?)", repeaterId);
            SendTo(PacketUtils.BuildMstNak(repeaterId), repeater.EndPoint!);
        }
    }

    private void HandleRptPing(ReadOnlySpan<byte> payload, IPEndPoint endPoint)
    {
        if (payload.Length < 11) return; 
        var repeaterId = BinaryPrimitives.ReadInt32BigEndian(payload[7..11]);

        if (registry.TryGet(repeaterId, out var repeater) && repeater?.State == RepeaterState.LoggedIn)
        {
            Volatile.Write(ref repeater.LastPingTicks, DateTime.UtcNow.Ticks);
            
            //logger.LogDebug("<-- RPTPING | --> MSTPONG für {RepeaterId}", repeaterId);
            SendTo(PacketUtils.BuildMstPong(repeaterId), repeater.EndPoint!);
        }
        else
        {
            logger.LogWarning("RPTPING von nicht eingeloggtem Repeater {RepeaterId} erhalten", repeaterId);
            SendTo(PacketUtils.BuildMstNak(repeaterId), endPoint);
        }
    }

    private void HandleRptc(ReadOnlySpan<byte> payload, IPEndPoint endPoint)
    {
        bool isRptcl = payload.Length >= 5 && payload[4] == 0x4C;
        int offset = isRptcl ? 5 : 4; 
        
        if (payload.Length < offset + 4) return;
        
        var repeaterId = BinaryPrimitives.ReadInt32BigEndian(payload.Slice(offset, 4));

        if (!registry.TryGet(repeaterId, out var repeater)) return;

        if (isRptcl)
        {
            logger.LogInformation("<-- RPTCL (Disconnect) von ID {RepeaterId}", repeaterId);
            repeater.State = RepeaterState.Disconnected;
            Volatile.Write(ref repeater.LastPingTicks, 0);
        }
        else
        {
            logger.LogInformation("<-- RPTC (Config) von ID {RepeaterId}", repeaterId);
            logger.LogInformation("--> RPTACK (Config bestätigt für {RepeaterId})", repeaterId);
            
            SendTo(PacketUtils.BuildRptAck((uint)repeaterId), repeater.EndPoint ?? endPoint);
        }
    }

    // Zero-Allocation SendTo Implementierung für das IDmrSender Interface
    public void SendTo(ReadOnlySpan<byte> data, IPEndPoint endPoint)
    {
        if (_udpClient == null) return;

        try
        {
            _udpClient.Client.SendTo(data, SocketFlags.None, endPoint);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fehler beim Senden an {Endpoint}", endPoint);
        }
    }
}