using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using DMRoute_ng.Registry;
using DMRoute_ng.Routing;
using DMRoute_ng.Types;
using DMRoute_ng.Utils;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DMRoute_ng.Core;

public sealed partial class DmrServer(ILogger<DmrServer> logger, RepeaterRegistry registry, MicroSubnetRouter router)
    : BackgroundService, IDmrSender
{
    private const int DmrPort = 62031;
    private const int ReceiveBufferSize = 1024;

    private readonly byte[] _receiveBuffer = GC.AllocateUninitializedArray<byte>(ReceiveBufferSize, pinned: true);
    private readonly SocketAddress _receiveAddress = new(AddressFamily.InterNetwork, 16);
    private readonly SocketAddress _sendAddress = new(AddressFamily.InterNetwork, 16);
    private readonly object _sendLock = new();
    private Socket? _socket;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(new IPEndPoint(IPAddress.Any, DmrPort));
        logger.LogInformation("DMRoute_ng Server lauscht auf UDP Port {Port}", DmrPort);

        return Task.Factory.StartNew(
            () => ReceiveLoop(stoppingToken),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private void ReceiveLoop(CancellationToken stoppingToken)
    {
        var socket = _socket!;
        using var registration = stoppingToken.Register(static state => ((Socket)state!).Dispose(), socket);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (!socket.Poll(250_000, SelectMode.SelectRead)) continue;
                    var received = socket.ReceiveFrom(_receiveBuffer, SocketFlags.None, _receiveAddress);
                    var remoteEndPoint = Ipv4Endpoint.FromSocketAddress(_receiveAddress);
                    HandlePacket(_receiveBuffer.AsSpan(0, received), remoteEndPoint);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "Fehler beim Verarbeiten des UDP-Pakets");
                }
            }
        }
        catch (ObjectDisposedException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (SocketException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            socket.Dispose();
            _socket = null;
        }
    }

    internal void HandlePacket(ReadOnlySpan<byte> payload, Ipv4Endpoint remoteEndPoint)
    {
        if (payload.Length < 4) return;

        if (payload.StartsWith(PacketUtils.RptlHeader)) HandleRptl(payload, remoteEndPoint);
        else if (payload.StartsWith(PacketUtils.RptkHeader)) HandleRptk(payload);
        else if (payload.StartsWith(PacketUtils.RptPingHeader)) HandleRptPing(payload, remoteEndPoint);
        else if (payload.StartsWith(PacketUtils.DmrdHeader)) router.RouteDmrd(payload, remoteEndPoint, this);
        else if (payload.StartsWith(PacketUtils.RptcHeader)) HandleRptc(payload, remoteEndPoint);
        else if (payload.StartsWith(PacketUtils.DmrcHeader) && payload.Length >= 8)
        {
            var repeaterId = BinaryPrimitives.ReadInt32BigEndian(payload.Slice(4, 4));
            logger.LogInformation("<-- DMRC (Hotspot Config Update) von ID {RepeaterId}", repeaterId);
            SendRptAck((uint)repeaterId, remoteEndPoint);
        }
    }

    private void HandleRptl(ReadOnlySpan<byte> payload, Ipv4Endpoint endPoint)
    {
        if (payload.Length < 8) return;
        var repeaterId = BinaryPrimitives.ReadInt32BigEndian(payload[4..8]);

        if (!registry.TryGet(repeaterId, out var repeater))
        {
            logger.LogWarning("Repeater {RepeaterId} ist nicht registriert (Whitelist)", repeaterId);
            SendMstNak(repeaterId, endPoint);
            return;
        }

        var randomSalt = (uint)Random.Shared.Next();
        repeater.RandomNumber = randomSalt;
        repeater.EndPoint = endPoint;
        repeater.State = RepeaterState.ChallengeSent;
        SendRptAck(randomSalt, endPoint);
    }

    private void HandleRptk(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 40) return;

        var repeaterId = BinaryPrimitives.ReadInt32BigEndian(payload[4..8]);
        var receivedHash = payload[8..40];
        if (!registry.TryGetExisting(repeaterId, out var repeater) || repeater.EndPoint is not { } endPoint) return;

        var pskLength = Encoding.ASCII.GetByteCount(repeater.PreSharedKey);
        var rented = ArrayPool<byte>.Shared.Rent(4 + pskLength);
        try
        {
            var dataToHash = rented.AsSpan(0, 4 + pskLength);
            BinaryPrimitives.WriteUInt32BigEndian(dataToHash[..4], repeater.RandomNumber);
            Encoding.ASCII.GetBytes(repeater.PreSharedKey, dataToHash[4..]);

            Span<byte> calculatedHash = stackalloc byte[32];
            SHA256.HashData(dataToHash, calculatedHash);

            if (CryptographicOperations.FixedTimeEquals(calculatedHash, receivedHash))
            {
                repeater.State = RepeaterState.LoggedIn;
                Volatile.Write(ref repeater.LastPingTicks, DateTime.UtcNow.Ticks);
                registry.RefreshRoutingSnapshot();
                SendRptAck((uint)repeaterId, endPoint);
            }
            else
            {
                logger.LogWarning("--> MSTNAK (Hashes stimmen nicht überein für {RepeaterId}. Falsches Passwort?)", repeaterId);
                SendMstNak(repeaterId, endPoint);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    private void HandleRptPing(ReadOnlySpan<byte> payload, Ipv4Endpoint endPoint)
    {
        if (payload.Length < 11) return;
        var repeaterId = BinaryPrimitives.ReadInt32BigEndian(payload[7..11]);

        if (registry.TryGetExisting(repeaterId, out var repeater))
        {
            if (repeater is { State: RepeaterState.Disconnected, EndPoint: not null } && repeater.EndPoint.Value == endPoint)
            {
                logger.LogInformation("Soft-Reconnect durch Ping für Repeater {RepeaterId}", repeaterId);
                repeater.State = RepeaterState.LoggedIn;
                registry.RefreshRoutingSnapshot();
            }

            if (repeater.State == RepeaterState.LoggedIn && repeater.EndPoint is { } target)
            {
                Volatile.Write(ref repeater.LastPingTicks, DateTime.UtcNow.Ticks);
                SendMstPong(repeaterId, target);
                return;
            }
        }

        LogUnknownPing(logger, repeaterId);
        SendMstNak(repeaterId, endPoint);
    }

    private void HandleRptc(ReadOnlySpan<byte> payload, Ipv4Endpoint endPoint)
    {
        var isRptcl = payload.Length >= 5 && payload[4] == 0x4C;
        var offset = isRptcl ? 5 : 4;
        if (payload.Length < offset + 4) return;

        var repeaterId = BinaryPrimitives.ReadInt32BigEndian(payload.Slice(offset, 4));
        if (!registry.TryGetExisting(repeaterId, out var repeater)) return;

        if (isRptcl)
        {
            logger.LogInformation("<-- RPTCL (Disconnect) von ID {RepeaterId}", repeaterId);
            repeater.State = RepeaterState.Disconnected;
            Volatile.Write(ref repeater.LastPingTicks, 0);
            registry.RefreshRoutingSnapshot();
            return;
        }

        Volatile.Write(ref repeater.LastPingTicks, DateTime.UtcNow.Ticks);
        logger.LogInformation("<-- RPTC (Config) von ID {RepeaterId}", repeaterId);
        var configPayload = payload.Slice(offset + 4);
        if (!configPayload.IsEmpty)
        {
            try
            {
                var callsign = ReadFixedString(ref configPayload, 8);
                var rxFreq = ReadFixedString(ref configPayload, 9);
                var txFreq = ReadFixedString(ref configPayload, 9);
                _ = int.TryParse(ReadFixedString(ref configPayload, 2), out var txPower);
                _ = int.TryParse(ReadFixedString(ref configPayload, 2), out var colorCode);
                _ = float.TryParse(ReadFixedString(ref configPayload, 8), System.Globalization.CultureInfo.InvariantCulture, out var lat);
                _ = float.TryParse(ReadFixedString(ref configPayload, 9), System.Globalization.CultureInfo.InvariantCulture, out var lon);
                _ = int.TryParse(ReadFixedString(ref configPayload, 3), out var height);
                var location = ReadFixedString(ref configPayload, 20);
                var description = ReadFixedString(ref configPayload, 20);
                var url = ReadFixedString(ref configPayload, 124);
                var software = ReadFixedString(ref configPayload, 40);
                var package = ReadFixedString(ref configPayload, 40);

                repeater.Configuration = new RepeaterConfiguration(callsign, rxFreq, txFreq, txPower, colorCode,
                    lat, lon, height, location, description, url, software, package);
                logger.LogDebug("RPTC Metadaten für {Id} aktualisiert: {Callsign} / {Software}", repeaterId, callsign, software);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Fehler beim Parsen der RPTC-Metadaten für Repeater {Id}", repeaterId);
            }
        }

        logger.LogInformation("--> RPTACK (Config bestätigt für {RepeaterId})", repeaterId);
        SendRptAck((uint)repeaterId, repeater.EndPoint ?? endPoint);
    }

    public void SendTo(ReadOnlySpan<byte> data, Ipv4Endpoint endPoint)
    {
        var socket = _socket;
        if (socket is null) return;

        try
        {
            lock (_sendLock)
            {
                endPoint.WriteTo(_sendAddress);
                socket.SendTo(data, SocketFlags.None, _sendAddress);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fehler beim Senden an {Endpoint}", endPoint);
        }
    }

    private void SendRptAck(uint value, Ipv4Endpoint endPoint)
    {
        Span<byte> packet = stackalloc byte[PacketUtils.RptAckLength];
        PacketUtils.TryWriteRptAck(packet, value, out var written);
        SendTo(packet[..written], endPoint);
    }

    private void SendMstNak(int repeaterId, Ipv4Endpoint endPoint)
    {
        Span<byte> packet = stackalloc byte[PacketUtils.MstNakLength];
        PacketUtils.TryWriteMstNak(packet, repeaterId, out var written);
        SendTo(packet[..written], endPoint);
    }

    private void SendMstPong(int repeaterId, Ipv4Endpoint endPoint)
    {
        Span<byte> packet = stackalloc byte[PacketUtils.MstPongLength];
        PacketUtils.TryWriteMstPong(packet, repeaterId, out var written);
        SendTo(packet[..written], endPoint);
    }

    private static string ReadFixedString(ref ReadOnlySpan<byte> buffer, int length)
    {
        var field = buffer[..Math.Min(buffer.Length, length)];
        var value = Encoding.ASCII.GetString(field).Trim('\0', ' ');
        buffer = buffer.Length <= length ? default : buffer[length..];
        return value;
    }

    [LoggerMessage(2001, LogLevel.Warning, "RPTPING von völlig unbekanntem Repeater {RepeaterId} erhalten")]
    private static partial void LogUnknownPing(ILogger logger, int repeaterId);
}
