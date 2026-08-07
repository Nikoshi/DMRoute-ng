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

    // Speichert erwartete Blockanzahl und den Puffer pro DMR-ID
    private readonly ConcurrentDictionary<int, (int ExpectedBlocks, List<byte> Buffer)> _messageBuffers = new();

    public SdsGateway(ILogger<SdsGateway> logger, MicroSubnetRouter router)
    {
        _logger = logger;
        router.OnDataFrameReceived += HandleDataFrame;
    }

    // ReSharper disable once CognitiveComplexity
    private void HandleDataFrame(byte[] packet, string sourceEndpoint)
    {
        if (packet.Length < 53) return;

        var srcId = (packet[5] << 16) | (packet[6] << 8) | packet[7];
        var dataType = (byte)(packet[15] & 0x0F);
        var payload = packet.AsSpan(20, 33);

        if (dataType == 0x06) 
        {
            Span<byte> decodedHeader = stackalloc byte[12];
            Bptc19696.Decode(payload, decodedHeader);

            var expectedBlocks = decodedHeader[8] & 0x7F;
            //_logger.LogInformation("DATA HEADER (Confirmed) von {SrcId}. Erwartete Blöcke: {Blocks}. HEX: {Hex}", 
            //    srcId, expectedBlocks, BitConverter.ToString([.. decodedHeader]));

            _messageBuffers[srcId] = (expectedBlocks, []);
        }
        else if (dataType == 0x07 || dataType == 0x08) 
        {
            if (!_messageBuffers.TryGetValue(srcId, out var session)) return;

            byte[] decodedBlock;
            int blockSize;

            if (dataType == 0x07)
            {
                decodedBlock = DmrFecDecoder.Decode(payload, FallbackColorCode);
                blockSize = 12;
            }
            else // 0x08 - Trellis Rate 3/4
            {
                decodedBlock = new byte[18];
                if (!DmrTrellis.Decode(payload, decodedBlock))
                {
                    _logger.LogWarning("Trellis Fehlerkorrektur für Block von {SrcId} fehlgeschlagen", srcId);
                    return;
                }
                blockSize = 18;
            }
        
            lock (session.Buffer)
            {
                session.Buffer.AddRange(decodedBlock);
                //_logger.LogInformation("DATA BLOCK (Type {Type:X2}) von {SrcId}. Aktueller Buffer: {Count} Bytes. HEX: {Hex}", 
                //    dataType, srcId, session.Buffer.Count, BitConverter.ToString(session.Buffer.ToArray()));

                if (session.Buffer.Count >= session.ExpectedBlocks * blockSize)
                {
                    _logger.LogInformation("Alle Blöcke empfangen. Starte Dekodierung...");
                    
                    var fullMessage = session.Buffer.ToArray();
                    var ipOffset = -1;

                    // Suche den Start des IPv4-Headers in den ersten Bytes
                    for (var i = 0; i < 4; i++)
                    {
                        if (fullMessage[i] != 0x45 || fullMessage[i + 1] != 0x00) continue;
                        ipOffset = i;
                        break;
                    }

                    if (ipOffset >= 0)
                    {
                        // Type 07: TMS-Header endet 38 Bytes nach IP-Start
                        // Type 08: TMS-Header endet 42 Bytes nach IP-Start
                        var textOffset = ipOffset + (dataType == 0x07 ? 38 : 42); 
                        
                        // Verbleibende Länge abzüglich 4 Bytes CRC/Füllbytes am Ende
                        var textLength = fullMessage.Length - textOffset - 4; 
                        
                        if (textLength > 0)
                        {
                            var text = Encoding.Unicode.GetString(fullMessage, textOffset, textLength).TrimEnd('\0');
                            
                            // Verhindert leere Ausgaben bei reinen Delivery-ACKs
                            if (!string.IsNullOrWhiteSpace(text)) 
                            {
                                _logger.LogInformation("SMS von {SrcId} and {DstId}: {Text}", srcId, "(Dst fehlt noch!)", text);
                            }
                        }
                    }

                    _messageBuffers.TryRemove(srcId, out _);
                }
            }
        }
        else
        {
            _logger.LogInformation("Ignorierter DataType von {SrcId}: {Type:X2}", srcId, dataType);
        }
    }
}