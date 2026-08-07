using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
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

    private void HandleDataFrame(byte[] packet, string sourceEndpoint)
    {
        if (packet.Length < 55) return;

        int srcId = (packet[5] << 16) | (packet[6] << 8) | packet[7];
        byte dataType = (byte)(packet[15] & 0x0F);
        var payload = packet.AsSpan(20, 33);

        if (dataType == 0x06) // DT_DATA_HEADER
        {
            Span<byte> decodedHeader = stackalloc byte[12];
            Bptc19696.Decode(payload, decodedHeader);

            // Anzahl der Datenblöcke aus Byte 8 extrahieren (untere 7 Bits)
            int expectedBlocks = decodedHeader[8] & 0x7F;

            // Puffer für diese Übertragung initialisieren
            _messageBuffers[srcId] = (expectedBlocks, new List<byte>());
        }
        else if (dataType == 0x07 || dataType == 0x08) // DT_RATE_12_DATA oder DT_RATE_34_DATA
        {
            if (!_messageBuffers.TryGetValue(srcId, out var session)) return;

            byte[] decodedBlock = DmrFecDecoder.Decode(payload, FallbackColorCode);
            
            lock (session.Buffer)
            {
                session.Buffer.AddRange(decodedBlock);

                // Prüfen, ob alle Blöcke empfangen wurden (12 Bytes pro Block)
                if (session.Buffer.Count >= session.ExpectedBlocks * 12)
                {
                    byte[] fullMessage = session.Buffer.ToArray();
                    
                    // Offset: 20 Bytes IPv4 + 8 Bytes UDP + 10 Bytes TMS-Header = 38 Bytes
                    if (fullMessage.Length > 38)
                    {
                        // 4 Bytes CRC/Füllbytes am Ende ignorieren
                        int textLength = fullMessage.Length - 38 - 4; 
                        
                        if (textLength > 0)
                        {
                            // UTF-16 Little Endian decodieren
                            string text = Encoding.Unicode.GetString(fullMessage, 38, textLength);
                            
                            // Null-Terminierung am Ende entfernen, falls vorhanden
                            text = text.TrimEnd('\0');
                            
                            _logger.LogInformation("Nachricht von {SrcId} empfangen: {Text}", srcId, text);
                        }
                    }

                    // Puffer nach erfolgreicher Dekodierung leeren
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