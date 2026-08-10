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

    private readonly ConcurrentDictionary<int, (int ExpectedBlocks, List<byte> Buffer)> _messageBuffers = new();

    public event Action<int, string>? OnSmsReceived;
    
    public SdsGateway(ILogger<SdsGateway> logger, MicroSubnetRouter router)
    {
        _logger = logger;
        router.OnDataFrameReceived += HandleDataFrame;
    }

    private void HandleDataFrame(byte[] packet, string sourceEndpoint)
    {
        if (packet.Length < 53) return;

        var srcId = (packet[5] << 16) | (packet[6] << 8) | packet[7];
        var dataType = (byte)(packet[15] & 0x0F);
        
        // 0x06 = Data Header, 0x07 = 1/2 Rate Data, 0x08 = 3/4 Rate Data
        // Ignoriere jegliche Voice/Signalisierungs-Daten rigoros (ohne Log), da sie nun im Router abgehandelt werden
        if (dataType < 0x06 || dataType > 0x08) return;

        var payload = packet.AsSpan(20, 33);

        if (dataType == 0x06) 
        {
            Span<byte> decodedHeader = stackalloc byte[12];
            Bptc19696.Decode(payload, decodedHeader);
            var expectedBlocks = decodedHeader[8] & 0x7F;
            _messageBuffers[srcId] = (expectedBlocks, []);
        }
        else if (dataType is 0x07 or 0x08) 
        {
            if (!_messageBuffers.TryGetValue(srcId, out var session)) return;

            byte[] decodedBlock;
            int blockSize;

            if (dataType == 0x07)
            {
                decodedBlock = DmrFecDecoder.Decode(payload, FallbackColorCode);
                blockSize = 12;
            }
            else 
            {
                decodedBlock = new byte[18];
                if (!DmrTrellis.Decode(payload, decodedBlock)) return;
                blockSize = 18;
            }
        
            lock (session.Buffer)
            {
                session.Buffer.AddRange(decodedBlock);

                if (session.Buffer.Count >= session.ExpectedBlocks * blockSize)
                {
                    var fullMessage = session.Buffer.ToArray();
                    var ipOffset = -1;

                    for (var i = 0; i < 4; i++)
                    {
                        if (fullMessage[i] != 0x45 || fullMessage[i + 1] != 0x00) continue;
                        ipOffset = i;
                        break;
                    }

                    if (ipOffset >= 0)
                    {
                        var textOffset = ipOffset + (dataType == 0x07 ? 38 : 42); 
                        var textLength = fullMessage.Length - textOffset - 4; 
                        
                        if (textLength > 0)
                        {
                            var text = Encoding.Unicode.GetString(fullMessage, textOffset, textLength).TrimEnd('\0');
                            if (!string.IsNullOrWhiteSpace(text)) 
                            {
                                _logger.LogInformation("SMS von {SrcId}: {Text}", srcId, text);
                                if (!string.IsNullOrWhiteSpace(text)) 
                                {
                                    _logger.LogInformation("SMS von {SrcId}: {Text}", srcId, text);
                                    OnSmsReceived?.Invoke(srcId, text);
                                }
                            }
                        }
                    }
                    _messageBuffers.TryRemove(srcId, out _);
                }
            }
        }
    }
}