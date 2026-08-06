using System;
using System.Text;
using Microsoft.Extensions.Logging;
using DMRoute_ng.Routing;

namespace DMRoute_ng.Gateways;

public class SdsGateway
{
    private readonly ILogger<SdsGateway> _logger;

    public SdsGateway(ILogger<SdsGateway> logger, MicroSubnetRouter router)
    {
        _logger = logger;
        
        // Wir abonnieren das Event des Routers
        router.OnDataFrameReceived += HandleDataFrame;
    }

    private void HandleDataFrame(byte[] packet, string sourceEndpoint)
    {
        //_logger.LogDebug("RAW DATA: {Hex}", BitConverter.ToString(packet));
        
        // 1. Payload aus dem Homebrew-Paket extrahieren (ab Byte 16 bei DMRD)
        if (packet.Length < 49) return;
        var payload = packet.AsSpan(16, 33);

        // 2. ETSI DMR De-Interleaving & FEC Decoder aufrufen
        // byte[] decodedData = DmrFecDecoder.Decode(payload);

        // 3. proprietäres Format auswerten (z.B. Motorola TMS oder Hytera/Retevis)
        // string text = Encoding.BigEndianUnicode.GetString(decodedData);
    }
}