using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using DMRoute_ng.Coding;
using Microsoft.Extensions.Logging;
using DMRoute_ng.Routing;

namespace DMRoute_ng.Gateways;

public class SdsGateway
{
    private readonly ILogger<SdsGateway> _logger;
    private const byte FallbackColorCode = 1;

    // Tuple um dstId erweitert
    private readonly ConcurrentDictionary<int, (int ExpectedBlocks, int DstId, List<byte> Buffer)> _messageBuffers =
        new();

    // Event um Ziel-ID erweitert
    public event Action<int, int, string>? OnSmsReceived;

    public SdsGateway(ILogger<SdsGateway> logger, MicroSubnetRouter router)
    {
        _logger = logger;
        router.OnDataFrameReceived += HandleDataFrame;
    }

    private void HandleDataFrame(byte[] packet, string sourceEndpoint)
    {
        // 1. Minimum Size Check (53 Bytes DMRD Frame)
        if (packet.Length < 53) return;

        // Src und Dst stehen praktischerweise in JEDEM MMDVM-Frame!
        var srcId = (packet[5] << 16) | (packet[6] << 8) | packet[7];
        var dstId = (packet[8] << 16) | (packet[9] << 8) | packet[10];
        var dataType = (byte)(packet[15] & 0x0F);

        if (dataType is < 0x06 or > 0x08) return;

        var payload = packet.AsSpan(20, 33);

        if (dataType == 0x06) // Text Header
        {
            // Puffer hart zurücksetzen, ExpectedBlocks brauchen wir nicht mehr zwingend
            _messageBuffers[srcId] = (ExpectedBlocks: 0, DstId: dstId, Buffer: new List<byte>());
        }
        else if (dataType is 0x07 or 0x08) // Data Blöcke
        {
            // 🛠️ FALLBACK: Falls der 0x06 Header über RF verloren ging, 
            // erstellen wir die Session einfach jetzt. (Da Src/Dst sowieso bekannt sind!)
            if (!_messageBuffers.TryGetValue(srcId, out var session))
            {
                session = (ExpectedBlocks: 0, DstId: dstId, Buffer: new List<byte>());
                _messageBuffers[srcId] = session;
            }

            var blockSize = dataType == 0x07 ? 12 : 18;
            Span<byte> decodedData = stackalloc byte[blockSize];

            if (dataType == 0x07)
            {
                Bptc19696.Decode(payload, decodedData);
            }
            else
            {
                if (!DmrTrellis.Decode(payload, decodedData)) return;
            }

            // Dekodierte Daten in den Puffer schieben (mutiert die List<byte> im Dictionary)
            session.Buffer.AddRange(decodedData);

            var fullMessage = session.Buffer.ToArray();
            
// Konvertiert den Puffer allokationsfrei (bzw. extrem speicherschonend) direkt in Hex-Großbuchstaben
            string hexLog = Convert.ToHexString(fullMessage);

            _logger.LogInformation("DMR SDS Hex-Dump (Type {DataType:X2}, Length {Length}): {Hex}", 
                dataType, fullMessage.Length, hexLog);

            
            // 🔎 Suche den Start des IPv4-Headers (0x45)
            var ipOffset = Array.IndexOf(fullMessage, (byte)0x45);

            // Iteriere, falls 0x45 zufällig in einem Padding/CRC-Byte auftaucht
            while (ipOffset != -1 && ipOffset + 4 <= fullMessage.Length)
            {
                // Extrahiere die *wahre* Länge des IPv4-Pakets aus Byte 2 und 3
                var ipLength = (fullMessage[ipOffset + 2] << 8) | fullMessage[ipOffset + 3];

                // Plausibilitätscheck: IP (20) + UDP (8) + TMS Header (6) = mind. 34 Bytes
                if (ipLength >= 34 && ipLength <= 500)
                {
                    // 🔥 Der magische Moment: Haben wir genug Bytes gesammelt, um das IP-Paket zu füllen?
                    if (fullMessage.Length >= ipOffset + ipLength)
                    {
                        _messageBuffers.TryRemove(srcId, out _); // Aufräumen

                        // Prüfen auf Motorola TMS Port (4007)
                        var udpPort = (fullMessage[ipOffset + 22] << 8) | fullMessage[ipOffset + 23];
                        if (udpPort == 4007)
                        {
                            var udpPayloadOffset = ipOffset + 28;
                            var encodingByte = fullMessage[udpPayloadOffset + 5];
                            var textOffset = udpPayloadOffset + 6;

                            // Text-Länge ist exakt die IP-Länge minus alle IP/UDP/TMS Header (34 Bytes)
                            // => Dadurch bleibt die 4-Byte DMR CRC am Ende komplett unangetastet!
                            var textLength = ipLength - 34;

                            if (textLength > 0)
                            {
                                string text = string.Empty;
                                try
                                {
                                    if (encodingByte == 0x04)
                                    {
                                        if (textLength % 2 != 0) textLength--;
                                        text = Encoding.Unicode.GetString(fullMessage, textOffset, textLength);
                                    }
                                    else
                                    {
                                        text = Encoding.UTF8.GetString(fullMessage, textOffset, textLength);
                                    }

                                    text = text.Trim('\0', '\r', '\n');

                                    if (!string.IsNullOrWhiteSpace(text))
                                    {
                                        _logger.LogInformation("SMS von {SrcId} an {DstId} (Enc: 0x{Enc:X2}): {Text}",
                                            srcId, session.DstId, encodingByte, text);
                                        OnSmsReceived?.Invoke(srcId, session.DstId, text);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "Fehler beim Dekodieren der SMS (Encoding-Byte: 0x{Enc:X2})",
                                        encodingByte);
                                }
                            }
                        }

                        return; // Erfolgreich verarbeitet, Methode verlassen!
                    }
                }

                // Falls es ein falsches 0x45 war, weitersuchen
                ipOffset = Array.IndexOf(fullMessage, (byte)0x45, ipOffset + 1);
            }
        }
    }
}