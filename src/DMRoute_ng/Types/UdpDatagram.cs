using System.Buffers.Binary;

namespace DMRoute_ng.Types;

public ref struct UdpDatagram
{
    public bool IsValid { get; }
    public ushort SourcePort { get; }
    public ushort DestinationPort { get; }
    public int Length { get; }
    public ReadOnlySpan<byte> Payload { get; }

    public UdpDatagram(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8)
        {
            IsValid = false;
            SourcePort = 0;
            DestinationPort = 0;
            Length = 0;
            Payload = default;
            return;
        }

        SourcePort = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(0, 2));
        DestinationPort = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(2, 2));
        Length = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(4, 2));

        // UDP-Längenfeld validieren
        if (Length > data.Length || Length < 8)
        {
            IsValid = false;
            Payload = default;
        }
        else
        {
            IsValid = true;
            // UDP-Header (8 Bytes) abziehen
            Payload = data.Slice(8, Length - 8);
        }
    }
}