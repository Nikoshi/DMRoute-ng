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

public sealed partial class SdsEchoService : BackgroundService, ISdsService
{
    private const int MaxEchoCharacters = 120;
    private const int MaxEchoTextBytes = MaxEchoCharacters * 2;
    private const int ReplyBufferBytes = 320;
    private const int DatagramBufferBytes = 384;
    private const int FragmentBufferBytes = 384;
    private const int MaximumAttempts = 3;
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReplyDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan BurstDelay = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(2);

    private sealed class RequestSlot
    {
        private int _inUse;
        public readonly byte[] Text = GC.AllocateUninitializedArray<byte>(MaxEchoTextBytes, pinned: true);
        public int TextLength;
        public int SourceId;
        public int HotspotId;
        public long ReceiveDurationTicks;
        public bool TooLong;

        public bool TryRent() => Interlocked.CompareExchange(ref _inUse, 1, 0) == 0;
        public void Release()
        {
            TextLength = 0;
            SourceId = 0;
            HotspotId = 0;
            ReceiveDurationTicks = 0;
            TooLong = false;
            Volatile.Write(ref _inUse, 0);
        }
    }

    private struct DuplicateEntry
    {
        public int SourceId;
        public int TextLength;
        public ulong Hash;
        public long SeenAtTicks;
    }

    private readonly ILogger<SdsEchoService> _logger;
    private readonly RepeaterRegistry _repeaters;
    private readonly MicroSubnetRouter _router;
    private readonly IDmrSender _sender;
    private readonly TimeProvider _timeProvider;
    private readonly bool _enabled;
    private readonly RequestSlot[] _slots;
    private readonly DuplicateEntry[] _duplicates;
    private readonly Channel<int> _queue;
    private readonly object _stateLock = new();
    private int _nextDuplicateSlot;
    private int _currentTargetId;
    private int _responseState;
    private int _streamSequence;
    private int _packetSequence;
    private long _droppedRequests;
    private long _acceptedRequests;
    private long _duplicateRequests;
    private long _successfulReplies;
    private long _failedReplies;

    public SdsEchoService(
        ILogger<SdsEchoService> logger,
        RepeaterRegistry repeaters,
        MicroSubnetRouter router,
        IDmrSender sender,
        DmRouteSettings settings,
        TimeProvider? timeProvider = null)
    {
        _logger = logger;
        _repeaters = repeaters;
        _router = router;
        _sender = sender;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _enabled = settings.Services.Echo.Enabled;
        var capacity = settings.Sds.MaxSessions;
        _slots = new RequestSlot[capacity + 1];
        for (var i = 0; i < _slots.Length; i++) _slots[i] = new RequestSlot();
        _duplicates = new DuplicateEntry[capacity];
        _queue = Channel.CreateBounded<int>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite,
            AllowSynchronousContinuations = false
        });
        router.OnDataFrameReceived += HandleResponseFrame;
    }

    public int DestinationId => DmrServiceIds.Echo;
    public long DroppedRequests => Interlocked.Read(ref _droppedRequests);
    public long AcceptedRequests => Interlocked.Read(ref _acceptedRequests);
    public long DuplicateRequests => Interlocked.Read(ref _duplicateRequests);
    public long SuccessfulReplies => Interlocked.Read(ref _successfulReplies);
    public long FailedReplies => Interlocked.Read(ref _failedReplies);

    public void HandleMessage(in SdsMessageEvent message, ReadOnlySpan<byte> textBytes)
    {
        if (!_enabled || message.DestinationId != DestinationId || !message.IsGroupCall ||
            message.Encoding != TmsMessageWriter.Utf16LittleEndianEncoding ||
            !DmrServiceIds.IsFiveDigitRadio(message.SourceId)) return;

        textBytes = TrimTransportWhitespace(textBytes);
        if (!StartsWithEcho(textBytes)) return;
        var echoText = textBytes[10..];
        if (IsWhiteSpace(echoText)) return;

        var now = message.CompletedAtTicks;
        var hash = Hash(echoText);
        lock (_stateLock)
        {
            if (IsDuplicate(message.SourceId, echoText.Length, hash, now))
            {
                Interlocked.Increment(ref _duplicateRequests);
                return;
            }
            Remember(message.SourceId, echoText.Length, hash, now);

            var slotIndex = RentSlot();
            if (slotIndex < 0)
            {
                Interlocked.Increment(ref _droppedRequests);
                return;
            }

            var slot = _slots[slotIndex];
            slot.SourceId = message.SourceId;
            slot.HotspotId = message.HotspotId;
            slot.ReceiveDurationTicks = message.ReceiveDurationTicks;
            slot.TooLong = echoText.Length / 2 > MaxEchoCharacters;
            if (!slot.TooLong)
            {
                echoText.CopyTo(slot.Text);
                slot.TextLength = echoText.Length;
            }

            if (!_queue.Writer.TryWrite(slotIndex))
            {
                slot.Release();
                Interlocked.Increment(ref _droppedRequests);
            }
            else Interlocked.Increment(ref _acceptedRequests);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var slotIndex in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            var slot = _slots[slotIndex];
            try
            {
                await Task.Delay(ReplyDelay, _timeProvider, stoppingToken).ConfigureAwait(false);
                if (!TryGetTarget(slot.HotspotId, out var endpoint, out var colorCode))
                {
                    Interlocked.Increment(ref _failedReplies);
                    LogTargetUnavailable(_logger, slot.SourceId, slot.HotspotId);
                    continue;
                }

                if (SendReply(slot, endpoint, colorCode, stoppingToken))
                {
                    Interlocked.Increment(ref _successfulReplies);
                    LogReplyDelivered(_logger, slot.SourceId, slot.HotspotId);
                }
                else
                {
                    Interlocked.Increment(ref _failedReplies);
                    LogReplyFailed(_logger, slot.SourceId, MaximumAttempts);
                }
            }
            finally
            {
                slot.Release();
            }
        }
    }

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

        var streamId = unchecked((uint)Interlocked.Increment(ref _streamSequence));
        var sendSequence = (byte)(streamId & 0x07);
        for (var attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            Volatile.Write(ref _currentTargetId, slot.SourceId);
            Volatile.Write(ref _responseState, 0);
            SendTransaction(fragment[..fragmentLength], (byte)blocks, (byte)pad, sendSequence,
                slot.HotspotId, slot.SourceId, endpoint, colorCode, streamId, cancellationToken);

            var deadline = _timeProvider.GetUtcNow() + ResponseTimeout;
            while (_timeProvider.GetUtcNow() < deadline)
            {
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
        for (var preamble = 0; preamble < 4; preamble++)
        {
            DmrBurstWriter.WriteCsbkPreamble(burst, DestinationId, targetId, (byte)(blocks + 4 - preamble), colorCode);
            SendBurst(packet, burst, hotspotId, targetId, 0x03, streamId, endpoint);
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

    private static int BuildReplyText(RequestSlot slot, Span<byte> destination)
    {
        var offset = 0;
        if (slot.TooLong)
            AppendAscii("ERROR text too long (max 120) | RX "u8, destination, ref offset);
        else
        {
            AppendAscii("ECHO "u8, destination, ref offset);
            slot.Text.AsSpan(0, slot.TextLength).CopyTo(destination[offset..]);
            offset += slot.TextLength;
            AppendAscii(" | RX "u8, destination, ref offset);
        }

        Span<byte> duration = stackalloc byte[20];
        var milliseconds = Math.Max(0, slot.ReceiveDurationTicks / TimeSpan.TicksPerMillisecond);
        Utf8Formatter.TryFormat(milliseconds, duration, out var durationLength);
        AppendAscii(duration[..durationLength], destination, ref offset);
        AppendAscii(" ms"u8, destination, ref offset);
        return offset;
    }

    private static void AppendAscii(ReadOnlySpan<byte> ascii, Span<byte> destination, ref int offset)
    {
        foreach (var value in ascii)
        {
            destination[offset++] = value;
            destination[offset++] = 0;
        }
    }

    private static bool StartsWithEcho(ReadOnlySpan<byte> text) =>
        text.Length >= 10 && text[0] == (byte)'E' && text[1] == 0 &&
        text[2] == (byte)'C' && text[3] == 0 && text[4] == (byte)'H' && text[5] == 0 &&
        text[6] == (byte)'O' && text[7] == 0 && text[8] == (byte)' ' && text[9] == 0;

    private static ReadOnlySpan<byte> TrimTransportWhitespace(ReadOnlySpan<byte> text)
    {
        if ((text.Length & 1) != 0) text = text[..^1];
        while (text.Length >= 2 && ReadCodeUnit(text) is '\0' or '\r' or '\n') text = text[2..];
        while (text.Length >= 2 && ReadCodeUnit(text[^2..]) is '\0' or '\r' or '\n') text = text[..^2];
        return text;
    }

    private static bool IsWhiteSpace(ReadOnlySpan<byte> text)
    {
        if (text.IsEmpty) return true;
        for (var offset = 0; offset + 1 < text.Length; offset += 2)
        {
            if (ReadCodeUnit(text[offset..]) is not ('\0' or ' ' or '\t' or '\r' or '\n')) return false;
        }
        return true;
    }

    private static char ReadCodeUnit(ReadOnlySpan<byte> text) => (char)(text[0] | (text[1] << 8));

    private static ulong Hash(ReadOnlySpan<byte> text)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offsetBasis;
        foreach (var value in text) hash = (hash ^ value) * prime;
        return hash;
    }

    private bool IsDuplicate(int sourceId, int textLength, ulong hash, long now)
    {
        var cutoff = now - DuplicateWindow.Ticks;
        for (var i = 0; i < _duplicates.Length; i++)
        {
            ref var entry = ref _duplicates[i];
            if (entry.SeenAtTicks >= cutoff && entry.SourceId == sourceId &&
                entry.TextLength == textLength && entry.Hash == hash) return true;
        }
        return false;
    }

    private void Remember(int sourceId, int textLength, ulong hash, long now)
    {
        ref var entry = ref _duplicates[_nextDuplicateSlot];
        entry = new DuplicateEntry { SourceId = sourceId, TextLength = textLength, Hash = hash, SeenAtTicks = now };
        _nextDuplicateSlot = (_nextDuplicateSlot + 1) % _duplicates.Length;
    }

    private int RentSlot()
    {
        for (var i = 0; i < _slots.Length; i++) if (_slots[i].TryRent()) return i;
        return -1;
    }

    public override void Dispose()
    {
        _router.OnDataFrameReceived -= HandleResponseFrame;
        base.Dispose();
    }

    [LoggerMessage(3001, LogLevel.Warning, "SDS-Echo-Ziel {TargetId} ist am Hotspot {HotspotId} nicht mehr erreichbar")]
    private static partial void LogTargetUnavailable(ILogger logger, int targetId, int hotspotId);

    [LoggerMessage(3002, LogLevel.Information, "SDS-Echo an {TargetId} über Hotspot {HotspotId} wurde bestätigt")]
    private static partial void LogReplyDelivered(ILogger logger, int targetId, int hotspotId);

    [LoggerMessage(3003, LogLevel.Warning, "SDS-Echo an {TargetId} blieb nach {Attempts} Versuchen unbestätigt")]
    private static partial void LogReplyFailed(ILogger logger, int targetId, int attempts);
}
