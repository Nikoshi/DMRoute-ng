using System.Threading.Channels;
using DMRoute_ng.Routing;
using DMRoute_ng.Types;

namespace DMRoute_ng.Integration;

internal enum MqttTelemetryEventType : byte
{
    Call,
    Sms,
    Location,
    DiagnosticFrame
}

internal sealed class DiagnosticFrameSlot
{
    private int _inUse;

    public DiagnosticFrameSlot(int capacity) => Buffer = GC.AllocateUninitializedArray<byte>(capacity, pinned: true);

    public byte[] Buffer { get; }
    public int Length { get; set; }

    public bool TryRent() => Interlocked.CompareExchange(ref _inUse, 1, 0) == 0;

    public void Release()
    {
        Length = 0;
        Volatile.Write(ref _inUse, 0);
    }
}

internal sealed class SmsMessageSlot
{
    private int _inUse;

    public SmsMessageSlot(int capacity) => Buffer = GC.AllocateUninitializedArray<byte>(capacity, pinned: true);

    public byte[] Buffer { get; }
    public int Length { get; set; }

    public bool TryRent() => Interlocked.CompareExchange(ref _inUse, 1, 0) == 0;

    public void Release()
    {
        Length = 0;
        Volatile.Write(ref _inUse, 0);
    }
}

internal readonly struct MqttTelemetryEvent
{
    private MqttTelemetryEvent(
        MqttTelemetryEventType eventType,
        in DmrCallEvent call,
        in DmrLocationEvent location,
        int sourceId,
        int destinationId,
        int hotspotId,
        byte dataType,
        long occurredAtTicks,
        byte encoding,
        bool containsLocation,
        SmsMessageSlot? smsMessage,
        DiagnosticFrameSlot? diagnosticFrame)
    {
        EventType = eventType;
        Call = call;
        Location = location;
        SourceId = sourceId;
        DestinationId = destinationId;
        HotspotId = hotspotId;
        DataType = dataType;
        OccurredAtTicks = occurredAtTicks;
        Encoding = encoding;
        ContainsLocation = containsLocation;
        SmsMessage = smsMessage;
        DiagnosticFrame = diagnosticFrame;
    }

    public MqttTelemetryEventType EventType { get; }
    public DmrCallEvent Call { get; }
    public DmrLocationEvent Location { get; }
    public int SourceId { get; }
    public int DestinationId { get; }
    public int HotspotId { get; }
    public byte DataType { get; }
    public long OccurredAtTicks { get; }
    public byte Encoding { get; }
    public bool ContainsLocation { get; }
    public SmsMessageSlot? SmsMessage { get; }
    public DiagnosticFrameSlot? DiagnosticFrame { get; }

    public static MqttTelemetryEvent ForCall(in DmrCallEvent call) =>
        new(MqttTelemetryEventType.Call, call, default, 0, 0, 0, 0, 0, 0, false, null, null);

    public static MqttTelemetryEvent ForSms(
        in SdsMessageEvent message, SmsMessageSlot slot) =>
        new(MqttTelemetryEventType.Sms, default, default, message.SourceId, message.DestinationId,
            message.HotspotId, 0, message.CompletedAtTicks, message.Encoding, message.ContainsLocation, slot, null);

    public static MqttTelemetryEvent ForLocation(in DmrLocationEvent location) =>
        new(MqttTelemetryEventType.Location, default, location, 0, 0, 0, 0, 0, 0, false, null, null);

    public static MqttTelemetryEvent ForDiagnostic(
        int sourceId, int hotspotId, byte dataType, long occurredAtTicks, DiagnosticFrameSlot slot) =>
        new(MqttTelemetryEventType.DiagnosticFrame, default, default, sourceId, 0, hotspotId, dataType,
            occurredAtTicks, 0, false, null, slot);
}

internal sealed class MqttTelemetryQueue
{
    private readonly DiagnosticFrameSlot[] _diagnosticSlots;
    private readonly SmsMessageSlot[] _smsSlots;
    private readonly Channel<MqttTelemetryEvent> _eventChannel;
    private readonly Channel<MqttTelemetryEvent> _diagnosticChannel;
    private long _droppedEvents;

    public MqttTelemetryQueue(
        int eventCapacity, int diagnosticFrameCapacity, int maxDiagnosticFrameBytes, int maxSmsMessageBytes = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(eventCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(diagnosticFrameCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDiagnosticFrameBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSmsMessageBytes);

        // One slot may be in-flight in the single reader and another staging slot
        // lets a new frame enter a full DropOldest channel before eviction releases a slot.
        _diagnosticSlots = new DiagnosticFrameSlot[checked(diagnosticFrameCapacity + 2)];
        for (var i = 0; i < _diagnosticSlots.Length; i++)
            _diagnosticSlots[i] = new DiagnosticFrameSlot(maxDiagnosticFrameBytes);
        _smsSlots = new SmsMessageSlot[checked(eventCapacity + 2)];
        for (var i = 0; i < _smsSlots.Length; i++) _smsSlots[i] = new SmsMessageSlot(maxSmsMessageBytes);

        _eventChannel = Channel.CreateBounded<MqttTelemetryEvent>(new BoundedChannelOptions(eventCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = false
        }, OnDropped);
        _diagnosticChannel = Channel.CreateBounded<MqttTelemetryEvent>(new BoundedChannelOptions(diagnosticFrameCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = false
        }, OnDropped);
    }

    public ChannelReader<MqttTelemetryEvent> EventReader => _eventChannel.Reader;
    public ChannelReader<MqttTelemetryEvent> DiagnosticReader => _diagnosticChannel.Reader;
    public long DroppedEvents => Interlocked.Read(ref _droppedEvents);

    public void EnqueueCall(in DmrCallEvent call) => _eventChannel.Writer.TryWrite(MqttTelemetryEvent.ForCall(call));

    public void EnqueueSms(in SdsMessageEvent message, ReadOnlySpan<byte> textBytes)
    {
        SmsMessageSlot? rented = null;
        for (var i = 0; i < _smsSlots.Length; i++)
        {
            if (!_smsSlots[i].TryRent()) continue;
            rented = _smsSlots[i];
            break;
        }
        if (rented is null || textBytes.Length > rented.Buffer.Length)
        {
            rented?.Release();
            Interlocked.Increment(ref _droppedEvents);
            return;
        }

        textBytes.CopyTo(rented.Buffer);
        rented.Length = textBytes.Length;
        if (!_eventChannel.Writer.TryWrite(MqttTelemetryEvent.ForSms(in message, rented)))
        {
            rented.Release();
            Interlocked.Increment(ref _droppedEvents);
        }
    }

    public void EnqueueLocation(in DmrLocationEvent location) =>
        _eventChannel.Writer.TryWrite(MqttTelemetryEvent.ForLocation(location));

    public void EnqueueDiagnostic(
        ReadOnlySpan<byte> packet, int sourceId, int hotspotId, byte dataType, long occurredAtTicks)
    {
        DiagnosticFrameSlot? rented = null;
        for (var i = 0; i < _diagnosticSlots.Length; i++)
        {
            var candidate = _diagnosticSlots[i];
            if (!candidate.TryRent()) continue;
            rented = candidate;
            break;
        }

        if (rented is null)
        {
            Interlocked.Increment(ref _droppedEvents);
            return;
        }

        var length = Math.Min(packet.Length, rented.Buffer.Length);
        packet[..length].CopyTo(rented.Buffer);
        rented.Length = length;
        if (!_diagnosticChannel.Writer.TryWrite(MqttTelemetryEvent.ForDiagnostic(
                sourceId, hotspotId, dataType, occurredAtTicks, rented)))
        {
            rented.Release();
            Interlocked.Increment(ref _droppedEvents);
        }
    }

    public static void Release(in MqttTelemetryEvent item)
    {
        item.DiagnosticFrame?.Release();
        item.SmsMessage?.Release();
    }

    private void OnDropped(MqttTelemetryEvent item)
    {
        Release(item);
        Interlocked.Increment(ref _droppedEvents);
    }
}
