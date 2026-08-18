namespace DMRoute_ng.Types;

using System.Buffers.Binary;

public ref struct Ipv4Packet
{
    public bool IsValid { get; }
    public int HeaderLength { get; }
    public int TotalLength { get; }
    public ReadOnlySpan<byte> Payload { get; }

    public Ipv4Packet(ReadOnlySpan<byte> data)
    {
        // Minimum 20 Bytes für IPv4 und Versions-Check (oberes Nibble muss 4 sein)
        if (data.Length < 20 || (data[0] >> 4) != 4)
        {
            IsValid = false;
            HeaderLength = 0;
            TotalLength = 0;
            Payload = default;
            return;
        }

        // IHL (Internet Header Length) gibt die Anzahl der 32-Bit Wörter an
        HeaderLength = (data[0] & 0x0F) * 4;
        TotalLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(2, 2));

        if (TotalLength > data.Length || HeaderLength > TotalLength)
        {
            IsValid = false;
            Payload = default;
        }
        else
        {
            IsValid = true;
            // Payload exakt anhand der TotalLength isolieren (schneidet DMR-CRC/Padding ab)
            Payload = data.Slice(HeaderLength, TotalLength - HeaderLength);
        }
    }
}