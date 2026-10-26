namespace DMRoute_ng.Coding;

/// <summary>Erzeugt Link-Control- und Sprachbursts für unverschlüsselte DMR-Rufe.</summary>
public static class DmrVoiceBurstWriter
{
    private const ulong MobileStationVoiceSync = 0x7F7D5DD57DFDUL;

    public static void WriteLinkControl(
        Span<byte> burst, int sourceId, int destinationId, bool isGroupCall, bool terminator, byte colorCode = 1)
    {
        if (burst.Length < DmrBurstWriter.BurstLength)
            throw new ArgumentException("Ein DMR-Burst enthält 33 Byte.", nameof(burst));
        burst[..DmrBurstWriter.BurstLength].Clear();
        Span<byte> lc = stackalloc byte[12];
        lc[0] = isGroupCall ? (byte)0 : (byte)3;
        WriteId(lc[3..6], destinationId);
        WriteId(lc[6..9], sourceId);
        Span<byte> parity = stackalloc byte[3];
        WriteRs129(lc[..9], parity);
        var mask = terminator ? (byte)0x99 : (byte)0x96;
        lc[9] = (byte)(parity[2] ^ mask);
        lc[10] = (byte)(parity[1] ^ mask);
        lc[11] = (byte)(parity[0] ^ mask);
        Bptc19696.Encode(lc, burst);
        WriteSlotTypeAndDataSync(burst, colorCode, terminator ? (byte)2 : (byte)1);
    }

    public static bool TryReadLinkControl(
        ReadOnlySpan<byte> burst,
        bool terminator,
        out int sourceId,
        out int destinationId,
        out bool isGroupCall)
    {
        sourceId = 0;
        destinationId = 0;
        isGroupCall = false;
        if (burst.Length < DmrBurstWriter.BurstLength) return false;
        Span<byte> lc = stackalloc byte[12];
        Bptc19696.Decode(burst, lc);
        var mask = terminator ? (byte)0x99 : (byte)0x96;
        lc[9] ^= mask;
        lc[10] ^= mask;
        lc[11] ^= mask;
        Span<byte> parity = stackalloc byte[3];
        WriteRs129(lc[..9], parity);
        if (lc[9] != parity[2] || lc[10] != parity[1] || lc[11] != parity[0] || lc[1] != 0)
            return false;
        if (lc[0] == 0) isGroupCall = true;
        else if (lc[0] != 3) return false;
        destinationId = ReadId(lc[3..6]);
        sourceId = ReadId(lc[6..9]);
        return sourceId > 0 && destinationId > 0;
    }

    public static void WriteVoice(
        Span<byte> burst,
        ReadOnlySpan<byte> ambe,
        int sourceId,
        int destinationId,
        bool isGroupCall,
        byte voiceIndex,
        byte colorCode = 1)
    {
        if (voiceIndex > 5) throw new ArgumentOutOfRangeException(nameof(voiceIndex));
        burst[..DmrBurstWriter.BurstLength].Clear();
        DmrVoiceBurstCodec.WriteAmbePayload(burst, ambe);
        if (voiceIndex == 0)
        {
            WriteSync(burst, MobileStationVoiceSync);
            return;
        }

        Span<bool> embedded = stackalloc bool[128];
        EncodeEmbeddedLinkControl(embedded, sourceId, destinationId, isGroupCall);
        var block = voiceIndex - 1;
        if (block < 4)
        {
            for (var bit = 0; bit < 32; bit++) WriteBit(burst, 116 + bit, embedded[block * 32 + bit]);
        }
        var lcss = block switch { 0 => 1, 1 or 2 => 3, 3 => 2, _ => 0 };
        WriteEmbeddedSignalling(burst, colorCode, (byte)lcss);
    }

    /// <summary>Liest das Privacy-Bit der eingebetteten Signalisierung.</summary>
    public static bool TryReadPrivacyIndicator(ReadOnlySpan<byte> burst, out bool privacy)
    {
        if (burst.Length < DmrBurstWriter.BurstLength)
        {
            privacy = false;
            return false;
        }
        ushort received = 0;
        for (var bit = 0; bit < 8; bit++) received = (ushort)((received << 1) | (ReadBit(burst, 108 + bit) ? 1 : 0));
        for (var bit = 0; bit < 8; bit++) received = (ushort)((received << 1) | (ReadBit(burst, 148 + bit) ? 1 : 0));
        var bestDistance = 17;
        var bestValue = 0;
        for (var value = 0; value < 128; value++)
        {
            var distance = System.Numerics.BitOperations.PopCount((uint)(received ^ EncodeQr((byte)value)));
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            bestValue = value;
        }
        privacy = (bestValue & 0x04) != 0;
        return bestDistance <= 2;
    }

    private static void EncodeEmbeddedLinkControl(
        Span<bool> raw, int sourceId, int destinationId, bool isGroupCall)
    {
        Span<byte> lc = stackalloc byte[9];
        lc[0] = isGroupCall ? (byte)0 : (byte)3;
        WriteId(lc[3..6], destinationId);
        WriteId(lc[6..9], sourceId);
        Span<bool> data = stackalloc bool[128];
        var crc = 0;
        for (var index = 0; index < lc.Length; index++) crc += lc[index];
        crc %= 31;
        data[106] = (crc & 1) != 0;
        data[90] = (crc & 2) != 0;
        data[74] = (crc & 4) != 0;
        data[58] = (crc & 8) != 0;
        data[42] = (crc & 16) != 0;
        var sourceBit = 0;
        for (var row = 0; row < 7; row++)
        {
            var rowLength = row < 2 ? 11 : 10;
            for (var column = 0; column < rowLength; column++, sourceBit++)
                data[row * 16 + column] = ReadBit(lc, sourceBit);
            Hamming.Encode16114(data.Slice(row * 16, 16));
        }
        for (var column = 0; column < 16; column++)
            data[112 + column] = data[column] ^ data[16 + column] ^ data[32 + column] ^
                                 data[48 + column] ^ data[64 + column] ^ data[80 + column] ^ data[96 + column];
        var position = 0;
        for (var index = 0; index < 128; index++)
        {
            raw[index] = data[position];
            position += 16;
            if (position > 127) position -= 127;
        }
    }

    private static void WriteEmbeddedSignalling(Span<byte> burst, byte colorCode, byte lcss)
    {
        var value = (byte)(((colorCode & 0x0F) << 3) | (lcss & 0x03));
        var code16 = EncodeQr(value);
        for (var bit = 0; bit < 8; bit++) WriteBit(burst, 108 + bit, (code16 & (1 << (15 - bit))) != 0);
        for (var bit = 8; bit < 16; bit++) WriteBit(burst, 140 + bit, (code16 & (1 << (15 - bit))) != 0);
    }

    private static ushort EncodeQr(byte value)
    {
        var code15 = (ushort)(value << 8);
        var remainder = code15;
        for (var bit = 14; bit >= 8; bit--)
            if ((remainder & (1 << bit)) != 0) remainder ^= (ushort)(0x139 << (bit - 8));
        code15 |= (ushort)(remainder & 0xFF);
        var code16 = (ushort)(code15 << 1);
        if ((System.Numerics.BitOperations.PopCount(code15) & 1) != 0) code16 |= 1;
        return code16;
    }

    private static void WriteRs129(ReadOnlySpan<byte> data, Span<byte> parity)
    {
        parity.Clear();
        foreach (var value in data)
        {
            var feedback = (byte)(value ^ parity[2]);
            parity[2] = (byte)(parity[1] ^ Multiply(14, feedback));
            parity[1] = (byte)(parity[0] ^ Multiply(56, feedback));
            parity[0] = Multiply(64, feedback);
        }
    }

    private static byte Multiply(byte left, byte right)
    {
        var result = 0;
        var a = left;
        var b = right;
        while (b != 0)
        {
            if ((b & 1) != 0) result ^= a;
            var carry = (a & 0x80) != 0;
            a <<= 1;
            if (carry) a ^= 0x1D;
            b >>= 1;
        }
        return (byte)result;
    }

    private static void WriteSlotTypeAndDataSync(Span<byte> burst, byte colorCode, byte dataType)
    {
        Span<byte> slotType = stackalloc byte[3];
        slotType[0] = (byte)(((colorCode & 0x0F) << 4) | dataType);
        Golay2087.Encode(slotType);
        for (var bit = 0; bit < 20; bit++)
            WriteBit(burst, bit < 10 ? 98 + bit : 146 + bit, ReadBit(slotType, bit));
        WriteSync(burst, 0xD5D7F77FD757UL);
    }

    private static void WriteSync(Span<byte> burst, ulong sync)
    {
        for (var bit = 0; bit < 48; bit++) WriteBit(burst, 108 + bit, (sync & (1UL << (47 - bit))) != 0);
    }

    private static bool ReadBit(ReadOnlySpan<byte> data, int bit) => (data[bit >> 3] & (0x80 >> (bit & 7))) != 0;
    private static void WriteBit(Span<byte> data, int bit, bool value)
    {
        var mask = (byte)(0x80 >> (bit & 7));
        if (value) data[bit >> 3] |= mask;
        else data[bit >> 3] &= (byte)~mask;
    }
    private static void WriteId(Span<byte> destination, int id)
    {
        if (id is <= 0 or > 0xFFFFFF) throw new ArgumentOutOfRangeException(nameof(id));
        destination[0] = (byte)(id >> 16);
        destination[1] = (byte)(id >> 8);
        destination[2] = (byte)id;
    }
    private static int ReadId(ReadOnlySpan<byte> source) => (source[0] << 16) | (source[1] << 8) | source[2];
}
