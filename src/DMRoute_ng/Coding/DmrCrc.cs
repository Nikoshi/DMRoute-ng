using System.Buffers.Binary;

namespace DMRoute_ng.Coding;

public static class DmrCrc
{
    public static ushort Ccitt(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (var value in data)
        {
            crc ^= (ushort)(value << 8);
            for (var bit = 0; bit < 8; bit++)
                crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
        }
        return crc;
    }

    public static void WriteMaskedCcitt(Span<byte> destination, ReadOnlySpan<byte> data, ushort mask)
    {
        var crc = (ushort)(Ccitt(data) ^ 0xFFFF ^ mask);
        BinaryPrimitives.WriteUInt16BigEndian(destination, crc);
    }

    public static bool CheckMaskedCcitt(ReadOnlySpan<byte> dataWithCrc, ushort mask)
    {
        if (dataWithCrc.Length < 2) return false;
        var payload = dataWithCrc[..^2];
        var expected = (ushort)(Ccitt(payload) ^ 0xFFFF ^ mask);
        return BinaryPrimitives.ReadUInt16BigEndian(dataWithCrc[^2..]) == expected;
    }

    public static uint MessageCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0;
        for (var offset = 0; offset < data.Length; offset += 2)
        {
            crc = UpdateCrc32(crc, data[offset + 1]);
            crc = UpdateCrc32(crc, data[offset]);
        }
        return crc;
    }

    public static ushort ConfirmedBlockCrc9(ReadOnlySpan<byte> blockData, byte serialNumber)
    {
        ushort crc = 0;
        foreach (var value in blockData)
        {
            for (var bit = 7; bit >= 0; bit--) crc = UpdateCrc9(crc, (value >> bit) & 1);
        }
        for (var bit = 6; bit >= 0; bit--) crc = UpdateCrc9(crc, (serialNumber >> bit) & 1);

        // Inversion polynomial 0x1FF and the Rate-3/4 mask 0x1FF cancel each other.
        return crc;
    }

    private static ushort UpdateCrc9(ushort crc, int inputBit)
    {
        var high = (crc >> 8) & 1;
        crc = (ushort)((crc << 1) & 0x1FF);
        if ((high ^ inputBit) != 0) crc ^= 0x059;
        return crc;
    }

    private static uint UpdateCrc32(uint crc, byte value)
    {
        crc ^= (uint)value << 24;
        for (var bit = 0; bit < 8; bit++)
            crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7U : crc << 1;
        return crc;
    }
}
