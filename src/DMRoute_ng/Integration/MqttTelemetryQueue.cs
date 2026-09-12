using System.Threading.Channels;
using DMRoute_ng.Routing;

namespace DMRoute_ng.Integration;

internal enum MqttTelemetryEventType : byte
{
    Call,
    Sms,
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

internal readonly struct MqttTelemetryEvent
{
    private MqttTelemetryEvent(
        MqttTelemetryEventType eventType,
        in DmrCallEvent call,
        int sourceId,
        int destinationId,
        int hotspotId,
        byte dataType,
        long occurredAtTicks,
        string? message,
        DiagnosticFrameSlot? diagnosticFrame)
    {
        EventType = eventType;
        Call = call;
        SourceId = sourceId;
        DestinationId = destinationId;
        HotspotId = hotspotId;
        DataType = dataType;
        OccurredAtTicks = occurredAtTicks;
        Message = message;
        DiagnosticFrame = diagnosticFrame;
    }

    public MqttTelemetryEventType EventType { get; }
    public DmrCallEvent Call { get; }
    public int SourceId { get; }
    public int DestinationId { get; }
    public int HotspotId { get; }
    public byte DataType { get; }
    public long OccurredAtTicks { get; }
    public string? Message { get; }
    public DiagnosticFrameSlot? DiagnosticFrame { get; }

    public static MqttTelemetryEvent ForCall(in DmrCallEvent call) =>
        new(MqttTelemetryEventType.Call, call, 0, 0, 0, 0, 0, null, null);

    public static MqttTelemetryEvent ForSms(int sourceId, int destinationId, string message, long occurredAtTicks) =>
        new(MqttTelemetryEventType.Sms, default, sourceId, destinationId, 0, 0, occurredAtTicks, message, null);

    public static MqttTelemetryEvent ForDiagnostic(
        int sourceId, int hotspotId, byte dataType, long occurredAtTicks, DiagnosticFrameSlot slot) =>
        new(MqttTelemetryEventType.DiagnosticFrame, default, sourceId, 0, hotspotId, dataType,
            occurredAtTicks, null, slot);
}

internal sealed class MqttTelemetryQueue
{
    private readonly DiagnosticFrameSlot[] _diagnosticSlots;
    private readonly Channel<MqttTelemetryEvent> _eventChannel;
    private readonly Channel<MqttTelemetryEvent> _diagnosticChannel;
    private long _droppedEvents;

    public MqttTelemetryQueue(int eventCapacity, int diagnosticFrameCapacity, int maxDiagnosticFrameBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(eventCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(diagnosticFrameCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDiagnosticFrameBytes);

        // One slot may be in-flight in the single reader and another staging slot
        // lets a new frame enter a full DropOldest channel before eviction releases a slot.
        _diagnosticSlots = new DiagnosticFrameSlot[checked(diagnosticFrameCapacity + 2)];
        for (var i = 0; i < _diagnosticSlots.Length; i++)
            _diagnosticSlots[i] = new DiagnosticFrameSlot(maxDiagnosticFrameBytes);

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

    public void EnqueueSms(int sourceId, int destinationId, string message, long occurredAtTicks) =>
        _eventChannel.Writer.TryWrite(MqttTelemetryEvent.ForSms(sourceId, destinationId, message, occurredAtTicks));

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

    public static void Release(in MqttTelemetryEvent item) => item.DiagnosticFrame?.Release();

    private void OnDropped(MqttTelemetryEvent item)
    {
        Release(item);
        Interlocked.Increment(ref _droppedEvents);
    }
}
