using System.Buffers.Binary;
using System.Buffers.Text;
using System.Threading.Channels;
using DMRoute_ng.Coding;
using DMRoute_ng.Configuration;
using DMRoute_ng.Core;
using DMRoute_ng.Gateways;
using DMRoute_ng.Registry;
using DMRoute_ng.Routing;
using DMRoute_ng.Types;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DMRoute_ng.Services;

public sealed partial class SdsEchoService
{
    private bool SendReply(
        RequestSlot slot, Ipv4Endpoint endpoint, byte colorCode, CancellationToken cancellationToken)
    {
        Span<byte> reply = stackalloc byte[ReplyBufferBytes];
        var replyLength = BuildReplyText(slot, reply);
        Span<byte> datagram = stackalloc byte[DatagramBufferBytes];
        if (!TmsMessageWriter.TryWriteUtf16LittleEndian(
                datagram, DestinationId, slot.SourceId, reply[..replyLength], out var datagramLength)) return false;

        Span<byte> fragment = stackalloc byte[FragmentBufferBytes];
        var blocks = (datagramLength + 4 + 15) / 16;
        var pad = blocks * 16 - datagramLength - 4;
        datagram[..datagramLength].CopyTo(fragment);
        fragment.Slice(datagramLength, pad).Clear();
        var crcOffset = datagramLength + pad;
        var crc = DmrCrc.MessageCrc32(fragment[..crcOffset]);
        BinaryPrimitives.WriteUInt32LittleEndian(fragment[crcOffset..], crc);
        var fragmentLength = crcOffset + 4;

        var sendSequence = unchecked((byte)((Interlocked.Increment(ref _sendSequence) - 1) & 0x07));
        for (var attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            var streamId = unchecked((uint)Interlocked.Increment(ref _streamSequence));
            Volatile.Write(ref _currentTargetId, slot.SourceId);
            Volatile.Write(ref _responseState, 0);
            SendTransaction(fragment[..fragmentLength], (byte)blocks, (byte)pad, sendSequence,
                slot.HotspotId, slot.SourceId, endpoint, colorCode, streamId, cancellationToken);

            var deadline = _timeProvider.GetUtcNow() + ResponseTimeout;
            while (_timeProvider.GetUtcNow() < deadline)
            {
                // Zustand 1 kann sowohl aus einem vollständigen AnyTone-TMS-Report
                // als auch aus einem positiven RT3S-DMR-Response-Header stammen.
                var response = Volatile.Read(ref _responseState);
                if (response == 1)
                {
                    Volatile.Write(ref _currentTargetId, 0);
                    return true;
                }
                if (response == 2) break;
                Wait(TimeSpan.FromMilliseconds(20), cancellationToken);
            }
        }
        Volatile.Write(ref _currentTargetId, 0);
        return false;
    }

    private void SendTransaction(
        ReadOnlySpan<byte> fragment,
        byte blocks,
        byte pad,
        byte sendSequence,
        int hotspotId,
        int targetId,
        Ipv4Endpoint endpoint,
        byte colorCode,
        uint streamId,
        CancellationToken cancellationToken)
    {
        Span<byte> burst = stackalloc byte[DmrBurstWriter.BurstLength];
        Span<byte> packet = stackalloc byte[DmrBurstWriter.DmrdLength];
        for (var preamble = 0; preamble < PreambleCount; preamble++)
        {
            DmrBurstWriter.WriteCsbkPreamble(
                burst, DestinationId, targetId, (byte)(blocks + PreambleCount - preamble), colorCode);
            SendBurst(packet, burst, hotspotId, targetId, 0x03,
                unchecked((uint)Interlocked.Increment(ref _streamSequence)), endpoint);
            Wait(BurstDelay, cancellationToken);
        }

        DmrBurstWriter.WriteConfirmedDataHeader(
            burst, DestinationId, targetId, blocks, pad, sendSequence, colorCode);
        SendBurst(packet, burst, hotspotId, targetId, 0x06, streamId, endpoint);
        Wait(BurstDelay, cancellationToken);

        for (byte block = 0; block < blocks; block++)
        {
            DmrBurstWriter.WriteConfirmedRateThreeQuarterBlock(
                burst, fragment.Slice(block * 16, 16), block, colorCode);
            SendBurst(packet, burst, hotspotId, targetId, 0x08, streamId, endpoint);
            if (block + 1 < blocks)
                Wait(BurstDelay, cancellationToken);
        }
    }

    private static void Wait(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (cancellationToken.WaitHandle.WaitOne(delay)) cancellationToken.ThrowIfCancellationRequested();
    }

    private void SendBurst(
        Span<byte> packet,
        ReadOnlySpan<byte> burst,
        int hotspotId,
        int targetId,
        byte dataType,
        uint streamId,
        Ipv4Endpoint endpoint)
    {
        var packetSequence = unchecked((byte)Interlocked.Increment(ref _packetSequence));
        DmrBurstWriter.WriteDmrd(packet, burst, packetSequence, DestinationId, targetId, hotspotId, dataType, streamId);
        _sender.SendTo(packet, endpoint);
    }

    private void HandleResponseFrame(ReadOnlySpan<byte> packet)
    {
        var target = Volatile.Read(ref _currentTargetId);
        if (target == 0 || !DmrBurstWriter.TryReadConfirmedResponse(packet, target, DestinationId, out var acknowledged))
            return;
        Volatile.Write(ref _responseState, acknowledged ? 1 : 2);
    }

    private bool TryGetTarget(int hotspotId, out Ipv4Endpoint endpoint, out byte colorCode)
    {
        if (_repeaters.TryGetExisting(hotspotId, out var repeater) &&
            repeater.State == RepeaterState.LoggedIn && repeater.EndPoint is { } target)
        {
            endpoint = target;
            colorCode = (byte)(repeater.Configuration?.ColorCode ?? 1);
            return true;
        }
        endpoint = default;
        colorCode = 1;
        return false;
    }
}
