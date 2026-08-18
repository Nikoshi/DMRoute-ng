using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using DMRoute_ng.Coding;
using Microsoft.Extensions.Logging;
using DMRoute_ng.Routing;
using DMRoute_ng.Types;

namespace DMRoute_ng.Gateways;

public class SdsGateway
{
    private readonly ILogger<SdsGateway> _logger;
    private const byte FallbackColorCode = 1;

    // Neues Flag 'bool IsConfirmedData' hinzugefügt
    private readonly ConcurrentDictionary<int, (int ExpectedBlocks, int DstId, bool IsConfirmedData, List<byte> Buffer)>
        _messageBuffers = new();

    // Event um Ziel-ID erweitert
    public event Action<int, int, string>? OnSmsReceived;

    public SdsGateway(ILogger<SdsGateway> logger, MicroSubnetRouter router)
    {
        _logger = logger;
        router.OnDataFrameReceived += HandleDataFrame;
    }

    private void HandleDataFrame(byte[] packet, string sourceEndpoint)
    {
        if (packet.Length < 53) return;

        var srcId = (packet[5] << 16) | (packet[6] << 8) | packet[7];
        var dstId = (packet[8] << 16) | (packet[9] << 8) | packet[10];
        var dataType = (byte)(packet[15] & 0x0F);

        if (dataType is < 0x06 or > 0x08) return;

        var payload = packet.AsSpan(20, 33);

        if (dataType == 0x06) // Text Header
        {
            _messageBuffers[srcId] =
                (ExpectedBlocks: 0, DstId: dstId, IsConfirmedData: false, Buffer: new List<byte>());
        }
        else if (dataType is 0x07 or 0x08) // Data Blöcke
        {
            var blockSize = dataType == 0x07 ? 12 : 18;
            Span<byte> decodedData = stackalloc byte[blockSize];

            if (dataType == 0x07) Bptc19696.Decode(payload, decodedData);
            else if (!DmrTrellis.Decode(payload, decodedData)) return;

            if (!_messageBuffers.TryGetValue(srcId, out var session))
            {
                int ipIdx = decodedData.IndexOf((byte)0x45);
                if (ipIdx == -1) return; // Padding Block verwerfen

                bool isConfirmed = ipIdx == 2;
                session = (ExpectedBlocks: 0, DstId: dstId, IsConfirmedData: isConfirmed, Buffer: new List<byte>());
                _messageBuffers[srcId] = session;
            }

            if (session.Buffer.Count == 0 && decodedData.Length > 2 && decodedData[2] == 0x45)
            {
                session.IsConfirmedData = true;
                _messageBuffers[srcId] = session;
            }

            var startIndex = session.IsConfirmedData ? 2 : 0;
            for (int i = startIndex; i < decodedData.Length; i++)
            {
                session.Buffer.Add(decodedData[i]);
            }

            var fullMessage = session.Buffer.ToArray();
            var spanMessage = fullMessage.AsSpan();

            var ipOffset = spanMessage.IndexOf((byte)0x45);

            while (ipOffset != -1)
            {
                var ipSpan = spanMessage.Slice(ipOffset);
                var ipv4 = new Ipv4Packet(ipSpan);

                if (ipv4.IsValid)
                {
                    var udp = new UdpDatagram(ipv4.Payload);

                    if (udp.IsValid && (udp.SourcePort == 4007 || udp.DestinationPort == 4007))
                    {
                        var tms = new TmsMessage(udp.Payload);

                        if (tms.IsValid)
                        {
                            _messageBuffers.TryRemove(srcId, out _);

                            var textLength = tms.TextBytes.Length;
                            if (textLength > 0)
                            {
                                try
                                {
                                    string text;
                                    if (tms.EncodingByte == 0x04)
                                    {
                                        if (textLength % 2 != 0) textLength--;
                                        text = Encoding.Unicode.GetString(tms.TextBytes.Slice(0, textLength));
                                    }
                                    else
                                    {
                                        text = Encoding.UTF8.GetString(tms.TextBytes);
                                    }

                                    text = text.Trim('\0', '\r', '\n');

                                    if (!string.IsNullOrWhiteSpace(text))
                                    {
                                        _logger.LogInformation("SMS von {SrcId} an {DstId} (Enc: 0x{Enc:X2}): {Text}",
                                            srcId, session.DstId, tms.EncodingByte, text);
                                        OnSmsReceived?.Invoke(srcId, session.DstId, text);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "Fehler beim Dekodieren der SMS (Enc: 0x{Enc:X2})",
                                        tms.EncodingByte);
                                }
                            }
                        }

                        return;
                    }
                }

                // Weitersuchen, falls 0x45 ein Fehlfund war
                if (ipOffset + 1 < spanMessage.Length)
                {
                    var nextIdx = spanMessage.Slice(ipOffset + 1).IndexOf((byte)0x45);
                    ipOffset = nextIdx == -1 ? -1 : ipOffset + 1 + nextIdx;
                }
                else
                {
                    ipOffset = -1;
                }
            }
        }
    }
}