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

/// <summary>
/// Beantwortet gültige Gruppen-SDS an den lokalen Echo-Dienst mit einer bestätigten
/// privaten SDS an den Absender.
/// </summary>
/// <remarks>
/// Empfangsslots, Duplikatfenster und Sendewarteschlange sind vorab begrenzt. Die
/// Funktransaktionen laufen seriell im Hintergrund; der synchrone Empfangspfad kopiert
/// nur akzeptierte Nutzdaten in einen reservierten Slot.
/// </remarks>
public sealed partial class SdsEchoService : BackgroundService, ISdsService
{
    private const int MaxEchoCharacters = 120;
    private const int MaxEchoTextBytes = MaxEchoCharacters * 2;
    private const int ReplyBufferBytes = 320;
    private const int DatagramBufferBytes = 384;
    private const int FragmentBufferBytes = 384;
    private const int MaximumAttempts = 3;
    private const int PreambleCount = 5;
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReplyDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan BurstDelay = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(3);

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
    private int _sendSequence;
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

    /// <summary>
    /// Prüft und übernimmt einen Echo-Befehl. Der geliehene Textspan wird während des
    /// Aufrufs ausgewertet und bei Annahme in vorab reservierten Besitzspeicher kopiert.
    /// </summary>
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

    /// <summary>Ordnet einen positiven TMS-Zustellbericht der laufenden Antwort zu.</summary>
    public void HandleDeliveryReport(in SdsDeliveryReportEvent report)
    {
        var target = Volatile.Read(ref _currentTargetId);
        if (target == 0 || report.SourceId != target || report.DestinationId != DestinationId ||
            report.IsGroupCall || report.Status != TmsDeliveryStatus.Delivered) return;
        Volatile.Write(ref _responseState, 1);
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
