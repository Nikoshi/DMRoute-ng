using System.Buffers.Binary;

namespace DMRoute_ng.Gateways;

public static class TmsMessageWriter
{
    public const ushort VendorPort = 4007;
    public const byte Utf16LittleEndianEncoding = 0x04;
    private const int IpHeaderLength = 20;
    private const int UdpHeaderLength = 8;
    private const int TmsHeaderLength = 6;
    private const int LeadingLineBreakLength = 4;

    public static bool TryWriteUtf16LittleEndian(
        Span<byte> destination,
        int sourceId,
        int destinationId,
        ReadOnlySpan<byte> text,
        out int written)
    {
        written = 0;
        if ((text.Length & 1) != 0 || sourceId is < 0 or > 0xFFFFFF || destinationId is < 0 or > 0xFFFFFF)
            return false;

        var tmsLength = checked(TmsHeaderLength + LeadingLineBreakLength + text.Length);
        var udpLength = checked(UdpHeaderLength + tmsLength);
        var totalLength = checked(IpHeaderLength + udpLength);
        if (destination.Length < totalLength || tmsLength - 2 > ushort.MaxValue) return false;

        var packet = destination[..totalLength];
        packet.Clear();
        packet[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(packet[2..4], (ushort)totalLength);
        packet[8] = 64;
        packet[9] = 17;
        WriteDerivedAddress(packet[12..16], sourceId);
        WriteDerivedAddress(packet[16..20], destinationId);
        BinaryPrimitives.WriteUInt16BigEndian(packet[10..12], InternetChecksum(packet[..IpHeaderLength]));

        var udp = packet[IpHeaderLength..];
        BinaryPrimitives.WriteUInt16BigEndian(udp[0..2], VendorPort);
        BinaryPrimitives.WriteUInt16BigEndian(udp[2..4], VendorPort);
        BinaryPrimitives.WriteUInt16BigEndian(udp[4..6], (ushort)udpLength);
        var tms = udp[UdpHeaderLength..];
        BinaryPrimitives.WriteUInt16BigEndian(tms[0..2], (ushort)(tmsLength - 2));
        tms[2] = 0xE0;
        tms[3] = 0x00;
        tms[4] = 0x81;
        tms[5] = Utf16LittleEndianEncoding;
        tms[6] = 0x0D;
        tms[7] = 0x00;
        tms[8] = 0x0A;
        tms[9] = 0x00;
        text.CopyTo(tms[10..]);
        BinaryPrimitives.WriteUInt16BigEndian(udp[6..8], UdpChecksum(packet[12..20], udp));
        written = totalLength;
        return true;
    }

    private static void WriteDerivedAddress(Span<byte> destination, int id)
    {
        destination[0] = 12;
        destination[1] = (byte)(id >> 16);
        destination[2] = (byte)(id >> 8);
        destination[3] = (byte)id;
    }

    private static ushort UdpChecksum(ReadOnlySpan<byte> addresses, ReadOnlySpan<byte> udp)
    {
        uint sum = SumWords(addresses);
        sum += 17;
        sum += (uint)udp.Length;
        sum += SumWords(udp);
        return FinishChecksum(sum);
    }

    private static ushort InternetChecksum(ReadOnlySpan<byte> data) => FinishChecksum(SumWords(data));

    private static uint SumWords(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        var offset = 0;
        while (offset + 1 < data.Length)
        {
            sum += BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            offset += 2;
        }
        if (offset < data.Length) sum += (uint)data[offset] << 8;
        return sum;
    }

    private static ushort FinishChecksum(uint sum)
    {
        while ((sum >> 16) != 0) sum = (sum & 0xFFFF) + (sum >> 16);
        var result = (ushort)~sum;
        return result == 0 ? (ushort)0xFFFF : result;
    }
}
