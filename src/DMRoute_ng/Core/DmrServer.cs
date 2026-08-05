using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using DMRoute_ng.Registry;
using DMRoute_ng.Types;
using DMRoute_ng.Utils;

namespace DMRoute_ng.Core;

public class DmrServer(ILogger<DmrServer> logger, RepeaterRegistry registry) : BackgroundService
{
    private const int DmrPort = 62031;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var udpClient = new UdpClient(DmrPort);
        logger.LogInformation("DMRoute_ng Server lauscht auf UDP Port {Port}", DmrPort);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await udpClient.ReceiveAsync(stoppingToken);
                var payload = result.Buffer.AsSpan();

                HandlePacket(payload, result.RemoteEndPoint, udpClient);
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
    }

    private void HandlePacket(ReadOnlySpan<byte> payload, IPEndPoint remoteEndPoint, UdpClient udpClient)
    {
        if (payload.Length < 4) return;

        if (payload.StartsWith(PacketUtils.RptlHeader))
        {
            HandleRptl(payload, remoteEndPoint, udpClient);
        }
        else if (payload.StartsWith(PacketUtils.RptkHeader))
        {
            HandleRptk(payload, udpClient);
        }
        else if (payload.StartsWith(PacketUtils.RptPingHeader))
        {
            HandleRptPing(payload, remoteEndPoint, udpClient);
        }
        else if (payload.StartsWith(PacketUtils.DmrdHeader))
        {
            // TODO: Routing für Sprach-/Datenpakete (Schritt 3)
        }
        else if (payload.StartsWith(PacketUtils.RptcHeader))
        {
            // Konfigurationspaket auswerten/verwerfen (RPTC / RPTCL)
            logger.LogDebug("RPTC/RPTCL empfangen, wird noch nicht vollständig verarbeitet");
        }
    }

    private void HandleRptl(ReadOnlySpan<byte> payload, IPEndPoint endPoint, UdpClient udpClient)
    {
        if (payload.Length < 8) return;
        var repeaterId = BinaryPrimitives.ReadInt32BigEndian(payload[4..8]);

        logger.LogInformation("<-- RPTL von ID {RepeaterId}", repeaterId);

        if (!registry.TryGet(repeaterId, out var repeater) || repeater == null)
        {
            logger.LogWarning("Repeater {RepeaterId} ist nicht registriert (Whitelist)", repeaterId);
            SendTo(udpClient, PacketUtils.BuildMstNak(repeaterId), endPoint);
            return;
        }

        var randomSalt = (uint)Random.Shared.Next();
        repeater.RandomNumber = randomSalt;
        repeater.EndPoint = endPoint;
        repeater.State = RepeaterState.ChallengeSent;

        logger.LogInformation("--> MSTC (Challenge gesendet an {RepeaterId})", repeaterId);
        SendTo(udpClient, PacketUtils.BuildMstc(repeaterId, randomSalt), endPoint);
    }

    private void HandleRptk(ReadOnlySpan<byte> payload, UdpClient udpClient)
    {
        if (payload.Length < 40) return; // 4 Bytes Header + 4 Bytes ID + 32 Bytes SHA256 Hash

        var repeaterId = BinaryPrimitives.ReadInt32BigEndian(payload[4..8]);
        var receivedHash = payload[8..40];

        if (!registry.TryGet(repeaterId, out var repeater) || repeater == null) return;

        logger.LogInformation("<-- RPTK von ID {RepeaterId}", repeaterId);

        // Zero-Allocation Hash-Berechnung auf dem Stack
        var pskLength = Encoding.ASCII.GetByteCount(repeater.PreSharedKey);
        Span<byte> dataToHash = stackalloc byte[4 + pskLength];
        
        BinaryPrimitives.WriteUInt32BigEndian(dataToHash[..4], repeater.RandomNumber);
        Encoding.ASCII.GetBytes(repeater.PreSharedKey, dataToHash[4..]);

        Span<byte> calculatedHash = stackalloc byte[32];
        SHA256.HashData(dataToHash, calculatedHash);

        // Sicherer Byte-Vergleich
        if (CryptographicOperations.FixedTimeEquals(calculatedHash, receivedHash))
        {
            repeater.State = RepeaterState.LoggedIn;
            repeater.LastPing = DateTime.UtcNow;

            logger.LogInformation("--> MSTA (Repeater {RepeaterId} erfolgreich eingeloggt)", repeaterId);
            SendTo(udpClient, PacketUtils.BuildMsta(repeaterId), repeater.EndPoint!);
        }
        else
        {
            logger.LogWarning("--> MSTNAK (Hashes stimmen nicht überein für {RepeaterId}. Falsches Passwort?)", repeaterId);
            SendTo(udpClient, PacketUtils.BuildMstNak(repeaterId), repeater.EndPoint!);
        }
    }

    private void HandleRptPing(ReadOnlySpan<byte> payload, IPEndPoint endPoint, UdpClient udpClient)
    {
        if (payload.Length < 11) return; // "RPTPING" (7) + ID (4)
        var repeaterId = BinaryPrimitives.ReadInt32BigEndian(payload[7..11]);

        if (registry.TryGet(repeaterId, out var repeater) && repeater?.State == RepeaterState.LoggedIn)
        {
            repeater.LastPing = DateTime.UtcNow;
            
            // Ping-Spam im Log vermeiden, ggf. LogDebug nutzen
            logger.LogDebug("<-- RPTPING | --> MSTPONG für {RepeaterId}", repeaterId);
            SendTo(udpClient, PacketUtils.BuildMstPong(repeaterId), repeater.EndPoint!);
        }
        else
        {
            logger.LogWarning("RPTPING von nicht eingeloggtem Repeater {RepeaterId} erhalten", repeaterId);
            SendTo(udpClient, PacketUtils.BuildMstNak(repeaterId), endPoint);
        }
    }

    private void SendTo(UdpClient client, byte[] data, IPEndPoint endPoint)
    {
        try
        {
            client.Send(data, data.Length, endPoint);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fehler beim Senden an {Endpoint}", endPoint);
        }
    }
}