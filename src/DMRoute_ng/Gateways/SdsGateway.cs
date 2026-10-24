using System.Buffers.Binary;
using DMRoute_ng.Coding;
using DMRoute_ng.Routing;
using DMRoute_ng.Services;
using DMRoute_ng.Types;
using Microsoft.Extensions.Logging;

namespace DMRoute_ng.Gateways;

/// <summary>
/// Setzt begrenzt viele eingehende SDS-Datenströme zusammen und veröffentlicht
/// vollständige TMS-Nachrichten, Zustellberichte und Positionsmeldungen.
/// </summary>
public sealed class SdsGateway
{
    private struct SessionState
    {
        public int SourceId;
        public int DestinationId;
        public int HotspotId;
        public int Length;
        public long LastSeenTicks;
        public long StartedAtTicks;
        public bool IsConfirmedData;
        public bool IsGroupCall;
        public bool Active;
    }

    private readonly ILogger<SdsGateway> _logger;
    private readonly int _maxMessageBytes;
    private readonly Dictionary<int, int> _sessionSlots;
    private readonly SessionState[] _sessions;
    private readonly byte[] _messageStorage;
    private readonly TimeProvider _timeProvider;
    private long _nextCleanupTicks;
    private long _droppedSessions;
    private long _oversizedMessages;

    /// <summary>
    /// Verarbeitet eine vollständige SDS. <paramref name="textBytes"/> verweist auf
    /// Gateway-Speicher und ist nur während des synchronen Rückrufs gültig.
    /// </summary>
    public delegate void SdsMessageHandler(in SdsMessageEvent message, ReadOnlySpan<byte> textBytes);
    public delegate void SdsLocationHandler(in DmrLocationEvent location);
    public delegate void SdsDeliveryReportHandler(in SdsDeliveryReportEvent report);

    public event SdsMessageHandler? OnMessageReceived;
    public event SdsLocationHandler? OnLocationReceived;
    public event SdsDeliveryReportHandler? OnDeliveryReportReceived;
    public long DroppedSessions => Interlocked.Read(ref _droppedSessions);
    public long OversizedMessages => Interlocked.Read(ref _oversizedMessages);

    public SdsGateway(
        ILogger<SdsGateway> logger,
        MicroSubnetRouter router,
        int maxSessions = 128,
        int maxMessageBytes = 4096,
        TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSessions);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxMessageBytes, 64);
        _logger = logger;
        _maxMessageBytes = maxMessageBytes;
        _sessionSlots = new Dictionary<int, int>(maxSessions);
        _sessions = new SessionState[maxSessions];
        _messageStorage = GC.AllocateUninitializedArray<byte>(checked(maxSessions * maxMessageBytes));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _nextCleanupTicks = _timeProvider.GetUtcNow().AddSeconds(30).Ticks;
        router.OnDataFrameReceived += HandleDataFrame;
    }

    internal void HandleDataFrame(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 53) return;
        var now = _timeProvider.GetUtcNow().Ticks;
        if (now >= _nextCleanupTicks) CleanupExpired(now);

        var sourceId = (packet[5] << 16) | (packet[6] << 8) | packet[7];
        var destinationId = (packet[8] << 16) | (packet[9] << 8) | packet[10];
        var hotspotId = BinaryPrimitives.ReadInt32BigEndian(packet[11..15]);
        var dataType = (byte)(packet[15] & 0x0F);
        if (dataType == 0x03)
        {
            if (!DmrServiceIds.IsReserved(destinationId)) return;
            if (TryGetOrCreateSession(sourceId, destinationId, hotspotId, false, now, out var preambleSlot))
            {
                ref var preamble = ref _sessions[preambleSlot];
                preamble.DestinationId = destinationId;
                preamble.HotspotId = hotspotId;
                preamble.IsGroupCall = (packet[15] & 0x40) == 0;
                preamble.LastSeenTicks = now;
            }
            return;
        }
        if (dataType is < 0x06 or > 0x08) return;

        if (dataType == 0x06)
        {
            Span<byte> decodedHeader = stackalloc byte[12];
            Bptc19696.Decode(packet.Slice(20, 33), decodedHeader);
            if ((decodedHeader[0] & 0x0F) == 0x01) return;
            if (TryGetOrCreateSession(sourceId, destinationId, hotspotId, false, now, out var headerSlot))
            {
                ref var headerSession = ref _sessions[headerSlot];
                headerSession.Length = 0;
                headerSession.DestinationId = destinationId;
                headerSession.HotspotId = hotspotId;
                headerSession.IsGroupCall = (packet[15] & 0x40) == 0;
                headerSession.IsConfirmedData = false;
                headerSession.LastSeenTicks = now;
            }
            return;
        }

        var payload = packet.Slice(20, 33);
        var blockSize = dataType == 0x07 ? 12 : 18;
        Span<byte> decodedData = stackalloc byte[18];
        var decodedBlock = decodedData[..blockSize];
        if (dataType == 0x07) Bptc19696.Decode(payload, decodedBlock);
        else if (!DmrTrellis.Decode(payload, decodedBlock)) return;

        if (!_sessionSlots.TryGetValue(sourceId, out var slot))
        {
            var ipIndex = decodedBlock.IndexOf((byte)0x45);
            if (ipIndex < 0 || !TryGetOrCreateSession(sourceId, destinationId, hotspotId, ipIndex == 2, now, out slot)) return;
        }

        ref var session = ref _sessions[slot];
        if (session.Length == 0 && decodedBlock.Length > 2 && decodedBlock[2] == 0x45) session.IsConfirmedData = true;

        var startIndex = session.IsConfirmedData ? 2 : 0;
        var bytesToAppend = decodedBlock[startIndex..];
        if (session.Length + bytesToAppend.Length > _maxMessageBytes)
        {
            Interlocked.Increment(ref _oversizedMessages);
            ReleaseSession(slot);
            return;
        }

        var message = GetSessionBuffer(slot);
        bytesToAppend.CopyTo(message[session.Length..]);
        session.Length += bytesToAppend.Length;
        session.LastSeenTicks = now;

        if (LocationDecoder.TryDecodeNmeaRmc(message[..session.Length], out var nmeaLocation))
        {
            var locationEvent = CreateLocationEvent(in session, in nmeaLocation);
            ReleaseSession(slot);
            OnLocationReceived?.Invoke(in locationEvent);
            return;
        }

        if (TryDecodeDeliveryReport(message[..session.Length], out var deliveryStatus))
        {
            var report = new SdsDeliveryReportEvent(
                sourceId,
                session.DestinationId,
                session.HotspotId,
                session.IsGroupCall,
                deliveryStatus,
                now);
            OnDeliveryReportReceived?.Invoke(in report);
            ReleaseSession(slot);
            return;
        }

        if (TryDecodeMessage(message[..session.Length], out var encoding, out var textBytes))
        {
            var targetId = session.DestinationId;
            var containsLocation = LocationDecoder.LooksLikeLocationText(encoding, textBytes);
            var decodedLocation = LocationDecoder.TryDecodeAnytoneGpsText(encoding, textBytes, out var textLocation);
            var locationEvent = decodedLocation ? CreateLocationEvent(in session, in textLocation) : default;
            if (decodedLocation) OnLocationReceived?.Invoke(in locationEvent);
            var messageEvent = new SdsMessageEvent(
                sourceId,
                targetId,
                session.HotspotId,
                session.IsGroupCall,
                encoding,
                containsLocation,
                session.StartedAtTicks,
                now);
            OnMessageReceived?.Invoke(in messageEvent, textBytes);
            ReleaseSession(slot);
        }
    }

    private bool TryGetOrCreateSession(
        int sourceId, int destinationId, int hotspotId, bool isConfirmedData, long now, out int slot)
    {
        if (_sessionSlots.TryGetValue(sourceId, out slot)) return true;
        if (_sessionSlots.Count >= _sessions.Length)
        {
            Interlocked.Increment(ref _droppedSessions);
            slot = -1;
            return false;
        }

        for (slot = 0; slot < _sessions.Length; slot++)
        {
            if (_sessions[slot].Active) continue;
            _sessions[slot] = new SessionState
            {
                SourceId = sourceId,
                DestinationId = destinationId,
                HotspotId = hotspotId,
                IsConfirmedData = isConfirmedData,
                IsGroupCall = false,
                StartedAtTicks = now,
                LastSeenTicks = now,
                Active = true
            };
            _sessionSlots.Add(sourceId, slot);
            return true;
        }

        Interlocked.Increment(ref _droppedSessions);
        slot = -1;
        return false;
    }

    private Span<byte> GetSessionBuffer(int slot) => _messageStorage.AsSpan(slot * _maxMessageBytes, _maxMessageBytes);

    private void ReleaseSession(int slot)
    {
        _sessionSlots.Remove(_sessions[slot].SourceId);
        _sessions[slot] = default;
    }

    private void CleanupExpired(long now)
    {
        var cutoff = now - TimeSpan.FromSeconds(30).Ticks;
        for (var slot = 0; slot < _sessions.Length; slot++)
        {
            if (_sessions[slot].Active && _sessions[slot].LastSeenTicks < cutoff) ReleaseSession(slot);
        }
        _nextCleanupTicks = now + TimeSpan.FromSeconds(30).Ticks;
    }

    private static bool TryDecodeMessage(ReadOnlySpan<byte> message, out byte encoding, out ReadOnlySpan<byte> textBytes)
    {
        var ipOffset = message.IndexOf((byte)0x45);
        while (ipOffset >= 0)
        {
            var ipv4 = new Ipv4Packet(message[ipOffset..]);
            if (ipv4.IsValid)
            {
                var udp = new UdpDatagram(ipv4.Payload);
                if (udp.IsValid && (udp.SourcePort == 4007 || udp.DestinationPort == 4007))
                {
                    var tms = new TmsMessage(udp.Payload);
                    if (tms.IsValid)
                    {
                        encoding = tms.EncodingByte;
                        textBytes = tms.TextBytes;
                        return true;
                    }
                }
            }

            var next = message[(ipOffset + 1)..].IndexOf((byte)0x45);
            ipOffset = next < 0 ? -1 : ipOffset + 1 + next;
        }

        encoding = 0;
        textBytes = default;
        return false;
    }

    private static bool TryDecodeDeliveryReport(
        ReadOnlySpan<byte> message, out TmsDeliveryStatus deliveryStatus)
    {
        var ipOffset = message.IndexOf((byte)0x45);
        while (ipOffset >= 0)
        {
            var ipv4 = new Ipv4Packet(message[ipOffset..]);
            if (ipv4.IsValid)
            {
                var udp = new UdpDatagram(ipv4.Payload);
                if (udp.IsValid && (udp.SourcePort == TmsMessageWriter.VendorPort ||
                                    udp.DestinationPort == TmsMessageWriter.VendorPort) &&
                    TmsDeliveryReport.TryRead(udp.Payload, out deliveryStatus))
                    return true;
            }

            var next = message[(ipOffset + 1)..].IndexOf((byte)0x45);
            ipOffset = next < 0 ? -1 : ipOffset + 1 + next;
        }

        deliveryStatus = default;
        return false;
    }

    internal void PublishSms(
        int sourceId, int destinationId, byte encoding, ReadOnlySpan<byte> textBytes, bool containsLocation = false)
    {
        if (textBytes.IsEmpty) return;
        var now = _timeProvider.GetUtcNow().Ticks;
        var message = new SdsMessageEvent(
            sourceId, destinationId, 0, false, encoding, containsLocation, now, now);
        OnMessageReceived?.Invoke(in message, textBytes);
    }

    private static DmrLocationEvent CreateLocationEvent(
        in SessionState session, in DecodedLocation location) => new(
        session.SourceId,
        session.DestinationId,
        session.HotspotId,
        location.Latitude,
        location.Longitude,
        location.SpeedMetersPerSecond,
        location.CourseDegrees,
        location.AltitudeMeters,
        location.FixValid,
        location.Format,
        session.LastSeenTicks);
}
