using System.Text;
using DMRoute_ng.Coding;
using DMRoute_ng.Routing;
using DMRoute_ng.Types;
using Microsoft.Extensions.Logging;

namespace DMRoute_ng.Gateways;

public sealed class SdsGateway
{
    private struct SessionState
    {
        public int SourceId;
        public int DestinationId;
        public int Length;
        public long LastSeenTicks;
        public bool IsConfirmedData;
        public bool Active;
    }

    private readonly ILogger<SdsGateway> _logger;
    private readonly int _maxMessageBytes;
    private readonly Dictionary<int, int> _sessionSlots;
    private readonly SessionState[] _sessions;
    private readonly byte[] _messageStorage;
    private long _nextCleanupTicks;
    private long _droppedSessions;
    private long _oversizedMessages;

    public event Action<int, int, string>? OnSmsReceived;
    public long DroppedSessions => Interlocked.Read(ref _droppedSessions);
    public long OversizedMessages => Interlocked.Read(ref _oversizedMessages);

    public SdsGateway(ILogger<SdsGateway> logger, MicroSubnetRouter router, int maxSessions = 128, int maxMessageBytes = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSessions);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxMessageBytes, 64);
        _logger = logger;
        _maxMessageBytes = maxMessageBytes;
        _sessionSlots = new Dictionary<int, int>(maxSessions);
        _sessions = new SessionState[maxSessions];
        _messageStorage = GC.AllocateUninitializedArray<byte>(checked(maxSessions * maxMessageBytes));
        _nextCleanupTicks = DateTime.UtcNow.AddSeconds(30).Ticks;
        router.OnDataFrameReceived += HandleDataFrame;
    }

    internal void HandleDataFrame(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 53) return;
        var now = DateTime.UtcNow.Ticks;
        if (now >= _nextCleanupTicks) CleanupExpired(now);

        var sourceId = (packet[5] << 16) | (packet[6] << 8) | packet[7];
        var destinationId = (packet[8] << 16) | (packet[9] << 8) | packet[10];
        var dataType = (byte)(packet[15] & 0x0F);
        if (dataType is < 0x06 or > 0x08) return;

        if (dataType == 0x06)
        {
            if (TryGetOrCreateSession(sourceId, destinationId, false, now, out var headerSlot))
            {
                ref var headerSession = ref _sessions[headerSlot];
                headerSession.Length = 0;
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
            if (ipIndex < 0 || !TryGetOrCreateSession(sourceId, destinationId, ipIndex == 2, now, out slot)) return;
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

        if (TryDecodeMessage(message[..session.Length], out var encoding, out var textBytes))
        {
            var targetId = session.DestinationId;
            ReleaseSession(slot);
            PublishSms(sourceId, targetId, encoding, textBytes);
        }
    }

    private bool TryGetOrCreateSession(int sourceId, int destinationId, bool isConfirmedData, long now, out int slot)
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
                IsConfirmedData = isConfirmedData,
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

    private void PublishSms(int sourceId, int destinationId, byte encoding, ReadOnlySpan<byte> textBytes)
    {
        if (textBytes.IsEmpty) return;
        try
        {
            string text;
            if (encoding == 0x04)
            {
                if ((textBytes.Length & 1) != 0) textBytes = textBytes[..^1];
                text = Encoding.Unicode.GetString(textBytes);
            }
            else text = Encoding.UTF8.GetString(textBytes);

            text = text.Trim('\0', '\r', '\n');
            if (string.IsNullOrWhiteSpace(text)) return;
            _logger.LogInformation("SMS von {SourceId} an {DestinationId} (Enc: 0x{Encoding:X2}): {Text}",
                sourceId, destinationId, encoding, text);
            OnSmsReceived?.Invoke(sourceId, destinationId, text);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler beim Dekodieren der SMS (Enc: 0x{Encoding:X2})", encoding);
        }
    }
}
