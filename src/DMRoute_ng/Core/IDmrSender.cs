using DMRoute_ng.Types;

namespace DMRoute_ng.Core;

/// <summary>
/// Sendet ein vollständiges DMR-Datagramm synchron an einen IPv4-Endpunkt.
/// </summary>
/// <remarks>
/// Der Aufrufer behält den Puffer. Implementierungen müssen den Inhalt vor der
/// Rückkehr vollständig verbrauchen oder kopieren und dürfen den Span nicht speichern.
/// </remarks>
public interface IDmrSender
{
    /// <summary>Sendet die im Span enthaltenen Bytes unverändert an den Zielendpunkt.</summary>
    void SendTo(ReadOnlySpan<byte> packet, Ipv4Endpoint endPoint);
}
