using System.Buffers.Binary;
using System.Text;
using System.Threading.Channels;
using DMRoute_ng.Coding;
using DMRoute_ng.Gateways;
using DMRoute_ng.Types;

namespace DMRoute_ng.Emulator;

public enum SdsTargetKind { Talkgroup, Radio }
public enum SdsSendStatus { Sent, Acknowledged, Rejected, TimedOut }
public enum RadioControlSendStatus { Acknowledged, TimedOut }
public enum RadioControlEventType
{
    CallAlertRequest,
    CallAlertAcknowledgement,
    RadioCheckRequest,
    RadioCheckAcknowledgement
}

public sealed record VirtualRadioOptions
{
    public required int RadioId { get; init; }
    public int Timeslot { get; init; } = 2;
    public int ColorCode { get; init; } = 1;
    public int[] Talkgroups { get; init; } = [];
    public int InboxCapacity { get; init; } = 128;
    public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromMilliseconds(2500);
    public int MaximumAttempts { get; init; } = 3;
}

public readonly record struct SdsMessage(
    int SourceId, int DestinationId, bool IsGroupCall, bool IsConfirmed, string Text, DateTimeOffset ReceivedAt);

public readonly record struct SdsSendResult(SdsSendStatus Status, int Attempts);
public readonly record struct RadioControlSendResult(RadioControlSendStatus Status, int Attempts);
public readonly record struct RadioControlEvent(
    RadioControlEventType Type, int SourceId, int DestinationId, DateTimeOffset ReceivedAt);

public sealed class VirtualRadio : IAsyncDisposable
{
    public const int MaximumTextCharacters = 120;
    private const int MaximumSessions = 8;
    private const int PreambleCount = 5;
    private static readonly TimeSpan BurstDelay = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan DeliveryReportDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ControlResponseDelay = TimeSpan.FromMilliseconds(120);

    private enum AutomaticReplyKind { ConfirmedData, DeliveryReport, CallAlert, RadioCheck }

    private readonly record struct AutomaticReply(
        AutomaticReplyKind Kind, int DestinationId, byte RequestSendSequence = 0);

    private sealed class ReassemblySession
    {
        public required int SourceId;
        public required int DestinationId;
        public required bool IsGroupCall;
        public required bool IsConfirmed;
        public required byte SendSequence;
        public required int Blocks;
        public required int Pad;
        public required DateTimeOffset ReceivedAt;
        public readonly byte[] Payload = new byte[4096];
        public int Length;
        public int ReceivedBlocks;
    }

    private readonly VirtualHotspot? _hotspot;
    private readonly HashSet<int> _talkgroups;
    private readonly Dictionary<uint, ReassemblySession> _sessions = new(MaximumSessions);
    private readonly Channel<SdsMessage> _inbox;
    private readonly Channel<RadioControlEvent> _controlInbox;
    private readonly Channel<AutomaticReply> _automaticReplies;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _transactionGate = new(1, 1);
    private readonly Task? _replyWorker;
    private readonly TimeSpan _responseTimeout;
    private readonly int _maximumAttempts;
    private readonly int _timeslot;
    private readonly byte _colorCode;
    private readonly int _inboxCapacity;
    private TaskCompletionSource<bool>? _pendingResponse;
    private TaskCompletionSource<bool>? _pendingControlResponse;
    private TaskCompletionSource<bool>? _pendingAnnouncement;
    private int _pendingResponseSource;
    private int _pendingControlResponseSource;
    private DmrControlMessageType _pendingControlResponseType;
    private uint _pendingAnnouncementStream;
    private int _streamSequence;
    private int _sendSequence;
    private int _identificationSequence;
    private int _packetSequence;
    private long _droppedMessages;
    private long _droppedControlEvents;
    private int _announced;
    private int _disposed;

    // Compatibility constructor for capture replay scenarios.
    public VirtualRadio(int radioId)
    {
        RadioId = ValidateRadioId(radioId);
        _talkgroups = [];
        _responseTimeout = TimeSpan.FromMilliseconds(2500);
        _maximumAttempts = 3;
        _timeslot = 2;
        _colorCode = 1;
        _inboxCapacity = 1;
        _inbox = CreateInbox(1);
        _controlInbox = CreateControlInbox(1);
        _automaticReplies = CreateReplyQueue();
    }

    public VirtualRadio(VirtualHotspot hotspot, VirtualRadioOptions options)
    {
        ArgumentNullException.ThrowIfNull(hotspot);
        ArgumentNullException.ThrowIfNull(options);
        RadioId = ValidateRadioId(options.RadioId);
        if (options.Timeslot is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(options.Timeslot));
        if (options.ColorCode is < 0 or > 15) throw new ArgumentOutOfRangeException(nameof(options.ColorCode));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.InboxCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumAttempts);
        if (options.ResponseTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options.ResponseTimeout));

        _hotspot = hotspot;
        _timeslot = options.Timeslot;
        _colorCode = (byte)options.ColorCode;
        _responseTimeout = options.ResponseTimeout;
        _maximumAttempts = options.MaximumAttempts;
        _inboxCapacity = options.InboxCapacity;
        _talkgroups = new HashSet<int>(options.Talkgroups);
        if (_talkgroups.Any(static id => id is <= 0 or > 0xFFFFFF))
            throw new ArgumentException("Talkgroup IDs must be between 1 and 16777215.", nameof(options));
        _inbox = CreateInbox(options.InboxCapacity);
        _controlInbox = CreateControlInbox(options.InboxCapacity);
        _automaticReplies = CreateReplyQueue();
        _hotspot.DmrdReceived += HandleDmrd;
        _replyWorker = SendAutomaticRepliesAsync(_lifetime.Token);
    }

    public int RadioId { get; }
    public int Timeslot => _timeslot;
    public IReadOnlyCollection<int> Talkgroups => _talkgroups;
    public long DroppedMessages => Interlocked.Read(ref _droppedMessages);
    public long DroppedControlEvents => Interlocked.Read(ref _droppedControlEvents);
    public int UnreadMessages => _inbox.Reader.Count;
    public int UnreadControlEvents => _controlInbox.Reader.Count;
    public bool IsAnnounced => Volatile.Read(ref _announced) != 0;

    public async Task ReplayAsync(VirtualHotspot hotspot, RadioScenario scenario,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hotspot);
        ArgumentNullException.ThrowIfNull(scenario);
        if (scenario.RadioId != RadioId)
            throw new ArgumentException("The scenario belongs to a different radio ID.", nameof(scenario));
        foreach (var step in scenario.Steps)
        {
            if (step.DelayBefore > TimeSpan.Zero)
                await Task.Delay(step.DelayBefore, hotspot.TimeProvider, cancellationToken).ConfigureAwait(false);
            await hotspot.SendDmrdAsync(step.Packet, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task AnnounceAsync(CancellationToken cancellationToken = default)
    {
        var hotspot = RequireAttachedHotspot();
        ThrowIfDisposed();
        await _transactionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stream = NextStreamId();
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingAnnouncementStream = stream;
            _pendingAnnouncement = completion;
            var packet = new byte[DmrBurstWriter.DmrdLength];
            var burst = new byte[DmrBurstWriter.BurstLength];
            DmrBurstWriter.WriteCsbkPreamble(burst, RadioId, RadioId, 0, _colorCode);
            DmrBurstWriter.WriteDmrd(packet, burst, NextPacketSequence(), RadioId, RadioId,
                hotspot.RepeaterId, 0x03, stream, _timeslot, false);
            await hotspot.SendDmrdAsync(packet, cancellationToken).ConfigureAwait(false);
            await completion.Task.WaitAsync(_responseTimeout, hotspot.TimeProvider, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _announced, 1);
        }
        finally
        {
            _pendingAnnouncement = null;
            _pendingAnnouncementStream = 0;
            _transactionGate.Release();
        }
    }

    public async Task<SdsSendResult> SendSdsAsync(SdsTargetKind targetKind, int destinationId, string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        var hotspot = RequireAttachedHotspot();
        ThrowIfDisposed();
        if (targetKind == SdsTargetKind.Radio) ValidateRadioId(destinationId);
        else if (destinationId is <= 0 or > 0xFFFFFF) throw new ArgumentOutOfRangeException(nameof(destinationId));
        if (text.Length is 0 or > MaximumTextCharacters)
            throw new ArgumentException($"SDS text must contain between 1 and {MaximumTextCharacters} characters.", nameof(text));

        var textBytes = Encoding.Unicode.GetBytes(text);
        var datagram = new byte[384];
        if (!TmsMessageWriter.TryWriteUtf16LittleEndian(datagram, RadioId, destinationId, textBytes, out var length))
            throw new InvalidOperationException("The SDS payload could not be encoded.");

        await _transactionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return targetKind == SdsTargetKind.Talkgroup
                ? await SendUnconfirmedAsync(hotspot, destinationId, datagram.AsMemory(0, length), cancellationToken)
                    .ConfigureAwait(false)
                : await SendConfirmedAsync(hotspot, destinationId, datagram.AsMemory(0, length), cancellationToken)
                    .ConfigureAwait(false);
        }
        finally
        {
            _pendingResponse = null;
            _pendingResponseSource = 0;
            _transactionGate.Release();
        }
    }

    public bool TryReceiveSds(out SdsMessage message) => _inbox.Reader.TryRead(out message);
    public ValueTask<SdsMessage> ReceiveSdsAsync(CancellationToken cancellationToken = default) =>
        _inbox.Reader.ReadAsync(cancellationToken);

    public int ClearInbox()
    {
        var count = 0;
        while (_inbox.Reader.TryRead(out _)) count++;
        return count;
    }

    public Task<RadioControlSendResult> SendCallAlertAsync(
        int destinationId, CancellationToken cancellationToken = default) =>
        SendControlAsync(DmrControlMessageType.CallAlertRequest, destinationId, cancellationToken);

    public Task<RadioControlSendResult> CheckRadioAsync(
        int destinationId, CancellationToken cancellationToken = default) =>
        SendControlAsync(DmrControlMessageType.RadioCheckRequest, destinationId, cancellationToken);

    public bool TryReceiveControlEvent(out RadioControlEvent controlEvent) =>
        _controlInbox.Reader.TryRead(out controlEvent);

    public ValueTask<RadioControlEvent> ReceiveControlEventAsync(CancellationToken cancellationToken = default) =>
        _controlInbox.Reader.ReadAsync(cancellationToken);

    public int ClearControlEvents()
    {
        var count = 0;
        while (_controlInbox.Reader.TryRead(out _)) count++;
        return count;
    }

    private async Task<RadioControlSendResult> SendControlAsync(
        DmrControlMessageType requestType, int destinationId, CancellationToken cancellationToken)
    {
        var hotspot = RequireAttachedHotspot();
        ThrowIfDisposed();
        ValidateRadioId(destinationId);
        var responseType = requestType == DmrControlMessageType.CallAlertRequest
            ? DmrControlMessageType.CallAlertAcknowledgement
            : DmrControlMessageType.RadioCheckAcknowledgement;

        await _transactionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var packet = new byte[DmrBurstWriter.DmrdLength];
            var burst = new byte[DmrBurstWriter.BurstLength];
            for (var attempt = 1; attempt <= _maximumAttempts; attempt++)
            {
                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingControlResponseSource = destinationId;
                _pendingControlResponseType = responseType;
                _pendingControlResponse = completion;
                await SendControlAttemptAsync(
                    hotspot, packet, burst, requestType, destinationId, cancellationToken).ConfigureAwait(false);
                try
                {
                    await completion.Task.WaitAsync(
                        _responseTimeout, hotspot.TimeProvider, cancellationToken).ConfigureAwait(false);
                    return new RadioControlSendResult(RadioControlSendStatus.Acknowledged, attempt);
                }
                catch (TimeoutException) when (attempt < _maximumAttempts)
                {
                }
                catch (TimeoutException)
                {
                    return new RadioControlSendResult(RadioControlSendStatus.TimedOut, attempt);
                }
            }
            return new RadioControlSendResult(RadioControlSendStatus.TimedOut, _maximumAttempts);
        }
        finally
        {
            _pendingControlResponse = null;
            _pendingControlResponseSource = 0;
            _transactionGate.Release();
        }
    }

    private async Task SendControlAttemptAsync(
        VirtualHotspot hotspot,
        byte[] packet,
        byte[] burst,
        DmrControlMessageType requestType,
        int destinationId,
        CancellationToken cancellationToken)
    {
        var preambleCount = requestType == DmrControlMessageType.CallAlertRequest ? 4 : 5;
        for (var preamble = preambleCount; preamble > 0; preamble--)
        {
            DmrBurstWriter.WriteCsbkPreamble(
                burst, RadioId, destinationId, (byte)preamble, _colorCode, false);
            await SendBurstAsync(hotspot, packet, burst, destinationId, 0x03, NextStreamId(), false,
                cancellationToken).ConfigureAwait(false);
            await Task.Delay(BurstDelay, hotspot.TimeProvider, cancellationToken).ConfigureAwait(false);
        }

        if (requestType == DmrControlMessageType.CallAlertRequest)
            DmrBurstWriter.WriteCallAlertRequest(burst, RadioId, destinationId, _colorCode);
        else
            DmrBurstWriter.WriteRadioCheckRequest(burst, RadioId, destinationId, _colorCode);
        await SendBurstAsync(hotspot, packet, burst, destinationId, 0x03, NextStreamId(), false,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<SdsSendResult> SendUnconfirmedAsync(VirtualHotspot hotspot, int destinationId,
        ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken)
    {
        var blocks = (datagram.Length + 21) / 18;
        var pad = blocks * 18 - datagram.Length - 4;
        var fragment = BuildFragment(datagram.Span, blocks * 18, pad);
        var stream = NextStreamId();
        await SendPreambleAsync(hotspot, destinationId, blocks, true, cancellationToken).ConfigureAwait(false);
        var packet = new byte[DmrBurstWriter.DmrdLength];
        var burst = new byte[DmrBurstWriter.BurstLength];
        DmrBurstWriter.WriteUnconfirmedDataHeader(burst, RadioId, destinationId,
            (byte)blocks, (byte)pad, true, _colorCode);
        await SendBurstAsync(hotspot, packet, burst, destinationId, 0x06, stream, true, cancellationToken)
            .ConfigureAwait(false);
        for (var block = 0; block < blocks; block++)
        {
            await Task.Delay(BurstDelay, hotspot.TimeProvider, cancellationToken).ConfigureAwait(false);
            DmrBurstWriter.WriteUnconfirmedRateThreeQuarterBlock(burst, fragment.AsSpan(block * 18, 18), _colorCode);
            await SendBurstAsync(hotspot, packet, burst, destinationId, 0x08, stream, true, cancellationToken)
                .ConfigureAwait(false);
        }
        return new SdsSendResult(SdsSendStatus.Sent, 1);
    }

    private async Task<SdsSendResult> SendConfirmedAsync(VirtualHotspot hotspot, int destinationId,
        ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken)
    {
        var blocks = (datagram.Length + 19) / 16;
        var pad = blocks * 16 - datagram.Length - 4;
        var fragment = BuildFragment(datagram.Span, blocks * 16, pad);
        var sendSequence = NextSendSequence();
        for (var attempt = 1; attempt <= _maximumAttempts; attempt++)
        {
            var stream = NextStreamId();
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingResponseSource = destinationId;
            _pendingResponse = completion;
            await SendPreambleAsync(hotspot, destinationId, blocks, false, cancellationToken).ConfigureAwait(false);
            var packet = new byte[DmrBurstWriter.DmrdLength];
            var burst = new byte[DmrBurstWriter.BurstLength];
            DmrBurstWriter.WriteConfirmedDataHeader(burst, RadioId, destinationId,
                (byte)blocks, (byte)pad, sendSequence, _colorCode);
            await SendBurstAsync(hotspot, packet, burst, destinationId, 0x06, stream, false, cancellationToken)
                .ConfigureAwait(false);
            for (byte block = 0; block < blocks; block++)
            {
                await Task.Delay(BurstDelay, hotspot.TimeProvider, cancellationToken).ConfigureAwait(false);
                DmrBurstWriter.WriteConfirmedRateThreeQuarterBlock(
                    burst, fragment.AsSpan(block * 16, 16), block, _colorCode);
                await SendBurstAsync(hotspot, packet, burst, destinationId, 0x08, stream, false, cancellationToken)
                    .ConfigureAwait(false);
            }
            try
            {
                var acknowledged = await completion.Task.WaitAsync(
                    _responseTimeout, hotspot.TimeProvider, cancellationToken).ConfigureAwait(false);
                if (acknowledged) return new SdsSendResult(SdsSendStatus.Acknowledged, attempt);
                if (attempt == _maximumAttempts) return new SdsSendResult(SdsSendStatus.Rejected, attempt);
            }
            catch (TimeoutException) when (attempt < _maximumAttempts) { }
            catch (TimeoutException) { return new SdsSendResult(SdsSendStatus.TimedOut, attempt); }
        }
        return new SdsSendResult(SdsSendStatus.TimedOut, _maximumAttempts);
    }

    private async Task SendPreambleAsync(VirtualHotspot hotspot, int destinationId, int blocks,
        bool isGroupCall, CancellationToken cancellationToken)
    {
        var packet = new byte[DmrBurstWriter.DmrdLength];
        var burst = new byte[DmrBurstWriter.BurstLength];
        for (var preamble = 0; preamble < PreambleCount; preamble++)
        {
            DmrBurstWriter.WriteCsbkPreamble(burst, RadioId, destinationId,
                (byte)(blocks + PreambleCount - preamble), _colorCode);
            await SendBurstAsync(hotspot, packet, burst, destinationId, 0x03, NextStreamId(), isGroupCall,
                    cancellationToken)
                .ConfigureAwait(false);
            await Task.Delay(BurstDelay, hotspot.TimeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SendBurstAsync(VirtualHotspot hotspot, byte[] packet, byte[] burst, int destinationId,
        byte dataType, uint stream, bool isGroupCall, CancellationToken cancellationToken)
    {
        DmrBurstWriter.WriteDmrd(packet, burst, NextPacketSequence(), RadioId, destinationId,
            hotspot.RepeaterId, dataType, stream, _timeslot, isGroupCall);
        await hotspot.SendDmrdAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    private void HandleDmrd(HomebrewDatagram datagram)
    {
        try
        {
            var packet = datagram.Payload.AsSpan();
            if (!HomebrewProtocol.TryReadDmrdInfo(packet, out var info) || info.Timeslot != _timeslot) return;
            if (_pendingAnnouncement is { } announcement && info.StreamId == _pendingAnnouncementStream &&
                info.SourceId == RadioId && info.DestinationId == RadioId && info.DataType == 0x03)
            {
                announcement.TrySetResult(true);
                return;
            }
            if (_pendingResponse is { } response && info.SourceId == _pendingResponseSource &&
                DmrBurstWriter.TryReadConfirmedResponse(packet, info.SourceId, RadioId, out var acknowledged))
            {
                response.TrySetResult(acknowledged);
                return;
            }
            if (DmrBurstWriter.TryReadControlMessage(packet, out var control) &&
                control.DestinationId == RadioId)
            {
                HandleControlMessage(in control, datagram.ReceivedAt);
                return;
            }
            var addressed = info.IsGroupCall ? _talkgroups.Contains(info.DestinationId) : info.DestinationId == RadioId;
            if (!addressed || !info.IsDataFrame) return;
            if (info.DataType == 0x06) BeginSession(packet, in info, datagram.ReceivedAt);
            else if (info.DataType == 0x08) AppendBlock(packet, in info);
        }
        catch (Exception)
        {
            // Malformed RF input must not terminate the hotspot receive loop.
        }
    }

    private void HandleControlMessage(in DmrControlMessage control, DateTimeOffset receivedAt)
    {
        var eventType = control.Type switch
        {
            DmrControlMessageType.CallAlertRequest => RadioControlEventType.CallAlertRequest,
            DmrControlMessageType.CallAlertAcknowledgement => RadioControlEventType.CallAlertAcknowledgement,
            DmrControlMessageType.RadioCheckRequest => RadioControlEventType.RadioCheckRequest,
            _ => RadioControlEventType.RadioCheckAcknowledgement
        };
        EnqueueControlEvent(new RadioControlEvent(
            eventType, control.SourceId, control.DestinationId, receivedAt));

        if (control.Type is DmrControlMessageType.CallAlertRequest or DmrControlMessageType.RadioCheckRequest)
        {
            var replyKind = control.Type == DmrControlMessageType.CallAlertRequest
                ? AutomaticReplyKind.CallAlert
                : AutomaticReplyKind.RadioCheck;
            _automaticReplies.Writer.TryWrite(new AutomaticReply(replyKind, control.SourceId));
            return;
        }

        if (_pendingControlResponse is { } response &&
            control.SourceId == _pendingControlResponseSource &&
            control.Type == _pendingControlResponseType)
            response.TrySetResult(true);
    }

    private void BeginSession(ReadOnlySpan<byte> packet, in DmrdFrameInfo info, DateTimeOffset receivedAt)
    {
        Span<byte> header = stackalloc byte[12];
        Bptc19696.Decode(packet.Slice(20, 33), header);
        var format = header[0] & 0x0F;
        if (format is not (0x02 or 0x03) || !DmrCrc.CheckMaskedCcitt(header, 0xCCCC)) return;
        if (_sessions.Count >= MaximumSessions && !_sessions.ContainsKey(info.StreamId))
            _sessions.Remove(_sessions.Keys.First());
        _sessions[info.StreamId] = new ReassemblySession
        {
            SourceId = info.SourceId,
            DestinationId = info.DestinationId,
            IsGroupCall = info.IsGroupCall,
            IsConfirmed = format == 0x03,
            SendSequence = (byte)((header[9] >> 4) & 0x07),
            Blocks = header[8] & 0x7F,
            Pad = (header[0] & 0x10) | (header[1] & 0x0F),
            ReceivedAt = receivedAt
        };
    }

    private void AppendBlock(ReadOnlySpan<byte> packet, in DmrdFrameInfo info)
    {
        if (!_sessions.TryGetValue(info.StreamId, out var session) ||
            session.SourceId != info.SourceId || session.DestinationId != info.DestinationId) return;
        Span<byte> decoded = stackalloc byte[18];
        if (!DmrTrellis.Decode(packet.Slice(20, 33), decoded)) { FinishInvalid(info.StreamId, session); return; }
        var blockOffset = 0;
        var blockLength = 18;
        if (session.IsConfirmed)
        {
            var serial = (byte)(decoded[0] >> 1);
            var crc = (ushort)(((decoded[0] & 1) << 8) | decoded[1]);
            blockOffset = 2;
            blockLength = 16;
            if (serial != session.ReceivedBlocks ||
                DmrCrc.ConfirmedBlockCrc9(decoded.Slice(blockOffset, blockLength), serial) != crc)
            { FinishInvalid(info.StreamId, session); return; }
        }
        if (session.Length + blockLength > session.Payload.Length) { FinishInvalid(info.StreamId, session); return; }
        decoded.Slice(blockOffset, blockLength).CopyTo(session.Payload.AsSpan(session.Length));
        session.Length += blockLength;
        session.ReceivedBlocks++;
        if (session.ReceivedBlocks < session.Blocks) return;

        _sessions.Remove(info.StreamId);
        var crcOffset = session.Length - 4;
        var payloadLength = crcOffset - session.Pad;
        var valid = payloadLength > 0 && crcOffset >= payloadLength &&
                    BinaryPrimitives.ReadUInt32LittleEndian(session.Payload.AsSpan(crcOffset, 4)) ==
                    DmrCrc.MessageCrc32(session.Payload.AsSpan(0, crcOffset));
        var deliveryStatus = default(TmsDeliveryStatus);
        var isDeliveryReport = valid && TryDecodeDeliveryReport(
            session.Payload.AsSpan(0, payloadLength), out deliveryStatus);
        if (isDeliveryReport)
        {
            if (_pendingResponse is { } response && session.SourceId == _pendingResponseSource &&
                session.DestinationId == RadioId && deliveryStatus == TmsDeliveryStatus.Delivered)
                response.TrySetResult(true);
        }
        else if (valid && TryDecodeText(session.Payload.AsSpan(0, payloadLength), out var text))
        {
            EnqueueMessage(new SdsMessage(session.SourceId, session.DestinationId, session.IsGroupCall,
                session.IsConfirmed, text, session.ReceivedAt));
        }
        if (session.IsConfirmed && !isDeliveryReport) QueueResponse(session, valid);
    }

    private void FinishInvalid(uint streamId, ReassemblySession session)
    {
        _sessions.Remove(streamId);
        if (session.IsConfirmed) QueueResponse(session, false);
    }

    private void QueueResponse(ReassemblySession session, bool acknowledged)
    {
        _automaticReplies.Writer.TryWrite(new AutomaticReply(
            acknowledged ? AutomaticReplyKind.DeliveryReport : AutomaticReplyKind.ConfirmedData,
            session.SourceId,
            session.SendSequence));
    }

    private async Task SendAutomaticRepliesAsync(CancellationToken cancellationToken)
    {
        if (_hotspot is null) return;
        var responsePacket = new byte[DmrBurstWriter.DmrdLength];
        var responseBurst = new byte[DmrBurstWriter.BurstLength];
        try
        {
            await foreach (var reply in _automaticReplies.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await _transactionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (reply.Kind == AutomaticReplyKind.DeliveryReport)
                    {
                        await Task.Delay(DeliveryReportDelay, _hotspot.TimeProvider, cancellationToken)
                            .ConfigureAwait(false);
                        await SendDeliveryReportAsync(_hotspot, reply.DestinationId, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else if (reply.Kind == AutomaticReplyKind.ConfirmedData)
                    {
                        DmrBurstWriter.WriteConfirmedResponseHeader(
                            responseBurst, RadioId, reply.DestinationId, reply.RequestSendSequence, false, _colorCode);
                        DmrBurstWriter.WriteDmrd(responsePacket, responseBurst, NextPacketSequence(), RadioId,
                            reply.DestinationId, _hotspot.RepeaterId, 0x06, NextStreamId(), _timeslot, false);
                        await _hotspot.SendDmrdAsync(responsePacket, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await Task.Delay(ControlResponseDelay, _hotspot.TimeProvider, cancellationToken)
                            .ConfigureAwait(false);
                        if (reply.Kind == AutomaticReplyKind.CallAlert)
                            DmrBurstWriter.WriteCallAlertAcknowledgement(
                                responseBurst, RadioId, reply.DestinationId, _colorCode);
                        else
                            DmrBurstWriter.WriteRadioCheckAcknowledgement(
                                responseBurst, RadioId, reply.DestinationId, _colorCode);
                        DmrBurstWriter.WriteDmrd(responsePacket, responseBurst, NextPacketSequence(), RadioId,
                            reply.DestinationId, _hotspot.RepeaterId, 0x03, NextStreamId(), _timeslot, false);
                        await _hotspot.SendDmrdAsync(responsePacket, cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _transactionGate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task SendDeliveryReportAsync(
        VirtualHotspot hotspot, int destinationId, CancellationToken cancellationToken)
    {
        var datagram = new byte[64];
        var identification = unchecked((ushort)Interlocked.Increment(ref _identificationSequence));
        if (!TmsMessageWriter.TryWriteDeliveryReport(
                datagram, RadioId, destinationId, identification, out var datagramLength))
            return;

        const int blocks = 3;
        const int pad = 11;
        var fragment = BuildFragment(datagram.AsSpan(0, datagramLength), blocks * 16, pad);
        var stream = NextStreamId();
        await SendPreambleAsync(hotspot, destinationId, blocks, false, cancellationToken)
            .ConfigureAwait(false);
        var packet = new byte[DmrBurstWriter.DmrdLength];
        var burst = new byte[DmrBurstWriter.BurstLength];
        DmrBurstWriter.WriteConfirmedDataHeader(
            burst, RadioId, destinationId, blocks, pad, NextSendSequence(), _colorCode);
        await SendBurstAsync(hotspot, packet, burst, destinationId, 0x06, stream, false, cancellationToken)
            .ConfigureAwait(false);
        for (byte block = 0; block < blocks; block++)
        {
            await Task.Delay(BurstDelay, hotspot.TimeProvider, cancellationToken).ConfigureAwait(false);
            DmrBurstWriter.WriteConfirmedRateThreeQuarterBlock(
                burst, fragment.AsSpan(block * 16, 16), block, _colorCode);
            await SendBurstAsync(hotspot, packet, burst, destinationId, 0x08, stream, false, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private void EnqueueMessage(SdsMessage message)
    {
        if (_inbox.Reader.Count >= _inboxCapacity) Interlocked.Increment(ref _droppedMessages);
        _inbox.Writer.TryWrite(message);
    }

    private void EnqueueControlEvent(RadioControlEvent controlEvent)
    {
        if (_controlInbox.Reader.Count >= _inboxCapacity) Interlocked.Increment(ref _droppedControlEvents);
        _controlInbox.Writer.TryWrite(controlEvent);
    }

    private static bool TryDecodeText(ReadOnlySpan<byte> data, out string text)
    {
        var offset = data.IndexOf((byte)0x45);
        while (offset >= 0)
        {
            var ipv4 = new Ipv4Packet(data[offset..]);
            if (ipv4.IsValid)
            {
                var udp = new UdpDatagram(ipv4.Payload);
                if (udp.IsValid && (udp.SourcePort == TmsMessageWriter.VendorPort ||
                                    udp.DestinationPort == TmsMessageWriter.VendorPort))
                {
                    var tms = new TmsMessage(udp.Payload);
                    if (tms.IsValid && tms.EncodingByte == TmsMessageWriter.Utf16LittleEndianEncoding)
                    {
                        text = Encoding.Unicode.GetString(TrimTransportWhitespace(tms.TextBytes));
                        return true;
                    }
                }
            }
            var next = data[(offset + 1)..].IndexOf((byte)0x45);
            offset = next < 0 ? -1 : offset + 1 + next;
        }
        text = string.Empty;
        return false;
    }

    private static bool TryDecodeDeliveryReport(
        ReadOnlySpan<byte> data, out TmsDeliveryStatus deliveryStatus)
    {
        var offset = data.IndexOf((byte)0x45);
        while (offset >= 0)
        {
            var ipv4 = new Ipv4Packet(data[offset..]);
            if (ipv4.IsValid)
            {
                var udp = new UdpDatagram(ipv4.Payload);
                if (udp.IsValid && (udp.SourcePort == TmsMessageWriter.VendorPort ||
                                    udp.DestinationPort == TmsMessageWriter.VendorPort) &&
                    TmsDeliveryReport.TryRead(udp.Payload, out deliveryStatus))
                    return true;
            }
            var next = data[(offset + 1)..].IndexOf((byte)0x45);
            offset = next < 0 ? -1 : offset + 1 + next;
        }
        deliveryStatus = default;
        return false;
    }

    private static ReadOnlySpan<byte> TrimTransportWhitespace(ReadOnlySpan<byte> text)
    {
        if ((text.Length & 1) != 0) text = text[..^1];
        while (text.Length >= 2 && (text[0] | text[1] << 8) is 0 or '\r' or '\n') text = text[2..];
        while (text.Length >= 2 && (text[^2] | text[^1] << 8) is 0 or '\r' or '\n') text = text[..^2];
        return text;
    }

    private static byte[] BuildFragment(ReadOnlySpan<byte> datagram, int length, int pad)
    {
        var fragment = new byte[length];
        datagram.CopyTo(fragment);
        var crcOffset = datagram.Length + pad;
        BinaryPrimitives.WriteUInt32LittleEndian(fragment.AsSpan(crcOffset),
            DmrCrc.MessageCrc32(fragment.AsSpan(0, crcOffset)));
        return fragment;
    }

    private static Channel<SdsMessage> CreateInbox(int capacity) =>
        Channel.CreateBounded<SdsMessage>(new BoundedChannelOptions(capacity)
        { SingleReader = false, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest });

    private static Channel<RadioControlEvent> CreateControlInbox(int capacity) =>
        Channel.CreateBounded<RadioControlEvent>(new BoundedChannelOptions(capacity)
        { SingleReader = false, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest });

    private static Channel<AutomaticReply> CreateReplyQueue() =>
        Channel.CreateBounded<AutomaticReply>(new BoundedChannelOptions(16)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropWrite });

    private VirtualHotspot RequireAttachedHotspot() =>
        _hotspot ?? throw new InvalidOperationException("This replay-only radio is not attached to a hotspot.");
    private static int ValidateRadioId(int radioId) => radioId is >= 10000 and <= 999991
        ? radioId : throw new ArgumentOutOfRangeException(nameof(radioId), "Radio IDs must contain five digits.");
    private byte NextPacketSequence() => unchecked((byte)Interlocked.Increment(ref _packetSequence));
    private uint NextStreamId() => unchecked((uint)Interlocked.Increment(ref _streamSequence));
    private byte NextSendSequence() => unchecked((byte)((Interlocked.Increment(ref _sendSequence) - 1) & 0x07));
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_hotspot is not null) _hotspot.DmrdReceived -= HandleDmrd;
        _lifetime.Cancel();
        _automaticReplies.Writer.TryComplete();
        if (_replyWorker is not null)
        {
            try { await _replyWorker.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _inbox.Writer.TryComplete();
        _controlInbox.Writer.TryComplete();
        _lifetime.Dispose();
        _transactionGate.Dispose();
    }
}
