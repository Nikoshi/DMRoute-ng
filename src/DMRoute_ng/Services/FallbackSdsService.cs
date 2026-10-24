using System.Buffers.Binary;
using System.Buffers.Text;
using System.Threading.Channels;
using DMRoute_ng.Coding;
using DMRoute_ng.Configuration;
using DMRoute_ng.Core;
using DMRoute_ng.Gateways;
using DMRoute_ng.Routing;
using DMRoute_ng.Types;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DMRoute_ng.Services;

public readonly record struct FallbackSdsRequest(
    ulong MessageId,
    int OwnerZone,
    int SourceId,
    int TargetId,
    RadioTargetRoute Route);

public readonly record struct FallbackSdsDelivery(
    ulong MessageId,
    int OwnerZone,
    bool Delivered);

internal readonly record struct FallbackSdsTiming(
    TimeSpan BurstDelay,
    TimeSpan ResponseTimeout,
    TimeSpan LateResponseGrace,
    TimeSpan PollInterval)
{
    public static FallbackSdsTiming Default { get; } = new(
        TimeSpan.FromMilliseconds(60),
        TimeSpan.FromMilliseconds(2500),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromMilliseconds(20));
}

public sealed partial class FallbackSdsService : BackgroundService, ISdsService
{
    private const int MaximumAttempts = 3;
    private const int PreambleCount = 5;
    private const int TextBufferBytes = 96;
    private const int DatagramBufferBytes = 192;
    private const int FragmentBufferBytes = 224;
    public delegate void DeliveryCompletedHandler(in FallbackSdsDelivery delivery);

    private readonly ILogger<FallbackSdsService> _logger;
    private readonly MicroSubnetRouter _router;
    private readonly IDmrSender _sender;
    private readonly TimeProvider _timeProvider;
    private readonly FallbackSdsTiming _timing;
    private readonly Channel<FallbackSdsRequest> _requests;
    private int _currentTargetId;
    private int _responseState;
    private int _streamSequence;
    private int _sendSequence;
    private int _packetSequence;
    private long _droppedRequests;

    public FallbackSdsService(
        ILogger<FallbackSdsService> logger,
        MicroSubnetRouter router,
        IDmrSender sender,
        DmRouteSettings settings,
        TimeProvider? timeProvider = null)
        : this(logger, router, sender, settings, timeProvider, FallbackSdsTiming.Default)
    {
    }

    internal FallbackSdsService(
        ILogger<FallbackSdsService> logger,
        MicroSubnetRouter router,
        IDmrSender sender,
        DmRouteSettings settings,
        TimeProvider? timeProvider,
        FallbackSdsTiming timing)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timing.BurstDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timing.ResponseTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timing.LateResponseGrace, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timing.PollInterval, TimeSpan.Zero);
        _logger = logger;
        _router = router;
        _sender = sender;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _timing = timing;
        DestinationId = settings.RadioCheck.FallbackServiceId;
        _requests = Channel.CreateBounded<FallbackSdsRequest>(new BoundedChannelOptions(settings.RadioCheck.FallbackCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        router.OnDataFrameReceived += HandleResponseFrame;
    }

    public int DestinationId { get; }
    public long DroppedRequests => Interlocked.Read(ref _droppedRequests);
    public event DeliveryCompletedHandler? DeliveryCompleted;

    public bool TryQueue(in FallbackSdsRequest request)
    {
        if (_requests.Writer.TryWrite(request)) return true;
        Interlocked.Increment(ref _droppedRequests);
        return false;
    }

    public void HandleMessage(in SdsMessageEvent message, ReadOnlySpan<byte> textBytes)
    {
    }

    public void HandleDeliveryReport(in SdsDeliveryReportEvent report)
    {
        var target = Volatile.Read(ref _currentTargetId);
        if (target == 0 || report.SourceId != target || report.DestinationId != DestinationId ||
            report.IsGroupCall || report.Status != TmsDeliveryStatus.Delivered) return;
        Volatile.Write(ref _responseState, 1);
    }

    private void HandleResponseFrame(ReadOnlySpan<byte> packet)
    {
        var target = Volatile.Read(ref _currentTargetId);
        if (target == 0 ||
            !DmrBurstWriter.TryReadConfirmedResponse(packet, target, DestinationId, out var acknowledged)) return;
        Volatile.Write(ref _responseState, acknowledged ? 1 : 2);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in _requests.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            var delivered = false;
            try
            {
                delivered = Send(in request, stoppingToken);
                if (delivered) LogDelivered(_logger, request.TargetId, request.SourceId);
                else LogFailed(_logger, request.TargetId, MaximumAttempts);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            finally
            {
                var completion = new FallbackSdsDelivery(request.MessageId, request.OwnerZone, delivered);
                DeliveryCompleted?.Invoke(in completion);
            }
        }
    }

    private bool Send(in FallbackSdsRequest request, CancellationToken cancellationToken)
    {
        Span<byte> text = stackalloc byte[TextBufferBytes];
        var textLength = BuildText(request.SourceId, text);
        Span<byte> datagram = stackalloc byte[DatagramBufferBytes];
        if (!TmsMessageWriter.TryWriteUtf16LittleEndian(
                datagram, DestinationId, request.TargetId, text[..textLength], out var datagramLength)) return false;

        Span<byte> fragment = stackalloc byte[FragmentBufferBytes];
        var blocks = (datagramLength + 4 + 15) / 16;
        var pad = blocks * 16 - datagramLength - 4;
        datagram[..datagramLength].CopyTo(fragment);
        fragment.Slice(datagramLength, pad).Clear();
        var crcOffset = datagramLength + pad;
        BinaryPrimitives.WriteUInt32LittleEndian(fragment[crcOffset..], DmrCrc.MessageCrc32(fragment[..crcOffset]));
        var sendSequence = unchecked((byte)((Interlocked.Increment(ref _sendSequence) - 1) & 0x07));

        Volatile.Write(ref _currentTargetId, request.TargetId);
        Volatile.Write(ref _responseState, 0);
        try
        {
            for (var attempt = 0; attempt < MaximumAttempts; attempt++)
            {
                if (Volatile.Read(ref _responseState) == 1) return true;
                SendTransaction(
                    fragment[..(crcOffset + 4)],
                    (byte)blocks,
                    (byte)pad,
                    sendSequence,
                    in request,
                    cancellationToken);
                if (WaitForAcknowledgement(_timing.ResponseTimeout, cancellationToken)) return true;
            }

            return WaitForAcknowledgement(_timing.LateResponseGrace, cancellationToken);
        }
        finally
        {
            Volatile.Write(ref _currentTargetId, 0);
        }
    }

    private bool WaitForAcknowledgement(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = _timeProvider.GetUtcNow() + timeout;
        while (_timeProvider.GetUtcNow() < deadline)
        {
            if (Volatile.Read(ref _responseState) == 1) return true;
            Wait(_timing.PollInterval, cancellationToken);
        }
        return Volatile.Read(ref _responseState) == 1;
    }

    private void SendTransaction(
        ReadOnlySpan<byte> fragment,
        byte blocks,
        byte pad,
        byte sendSequence,
        in FallbackSdsRequest request,
        CancellationToken cancellationToken)
    {
        Span<byte> burst = stackalloc byte[DmrBurstWriter.BurstLength];
        Span<byte> packet = stackalloc byte[DmrBurstWriter.DmrdLength];
        for (var preamble = 0; preamble < PreambleCount; preamble++)
        {
            DmrBurstWriter.WriteCsbkPreamble(
                burst, DestinationId, request.TargetId, (byte)(blocks + PreambleCount - preamble), request.Route.ColorCode);
            SendBurst(packet, burst, in request, 0x03, unchecked((uint)Interlocked.Increment(ref _streamSequence)));
            Wait(_timing.BurstDelay, cancellationToken);
        }

        var streamId = unchecked((uint)Interlocked.Increment(ref _streamSequence));
        DmrBurstWriter.WriteConfirmedDataHeader(
            burst, DestinationId, request.TargetId, blocks, pad, sendSequence, request.Route.ColorCode);
        SendBurst(packet, burst, in request, 0x06, streamId);
        Wait(_timing.BurstDelay, cancellationToken);

        for (byte block = 0; block < blocks; block++)
        {
            DmrBurstWriter.WriteConfirmedRateThreeQuarterBlock(
                burst, fragment.Slice(block * 16, 16), block, request.Route.ColorCode);
            SendBurst(packet, burst, in request, 0x08, streamId);
            if (block + 1 < blocks) Wait(_timing.BurstDelay, cancellationToken);
        }
    }

    private void SendBurst(
        Span<byte> packet,
        ReadOnlySpan<byte> burst,
        in FallbackSdsRequest request,
        byte dataType,
        uint streamId)
    {
        DmrBurstWriter.WriteDmrd(
            packet,
            burst,
            unchecked((byte)Interlocked.Increment(ref _packetSequence)),
            DestinationId,
            request.TargetId,
            request.Route.HotspotId,
            dataType,
            streamId);
        _sender.SendTo(packet, request.Route.EndPoint);
    }

    private static int BuildText(int sourceId, Span<byte> destination)
    {
        const string prefix = "Verbindungsversuch von ";
        var offset = 0;
        foreach (var character in prefix)
        {
            destination[offset++] = (byte)character;
            destination[offset++] = 0;
        }

        Span<byte> digits = stackalloc byte[12];
        Utf8Formatter.TryFormat(sourceId, digits, out var digitCount);
        for (var index = 0; index < digitCount; index++)
        {
            destination[offset++] = digits[index];
            destination[offset++] = 0;
        }
        return offset;
    }

    private static void Wait(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (cancellationToken.WaitHandle.WaitOne(delay)) cancellationToken.ThrowIfCancellationRequested();
    }

    public override void Dispose()
    {
        _router.OnDataFrameReceived -= HandleResponseFrame;
        base.Dispose();
    }

    [LoggerMessage(3101, LogLevel.Information,
        "Fallback-SDS an {TargetId} für Verbindungsversuch von {SourceId} wurde bestätigt")]
    private static partial void LogDelivered(ILogger logger, int targetId, int sourceId);

    [LoggerMessage(3102, LogLevel.Warning,
        "Fallback-SDS an {TargetId} nach {Attempts} Versuchen nicht bestätigt")]
    private static partial void LogFailed(ILogger logger, int targetId, int attempts);
}
