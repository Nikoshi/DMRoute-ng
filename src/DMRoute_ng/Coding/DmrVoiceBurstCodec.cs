namespace DMRoute_ng.Coding;

/// <summary>Packt drei kanonische 72-Bit-AMBE-Frames in einen DMR-Sprachburst.</summary>
public static class DmrVoiceBurstCodec
{
    public const int AmbeFrameLength = 9;
    public const int AmbeFramesPerBurst = 3;
    public const int AmbePayloadLength = AmbeFrameLength * AmbeFramesPerBurst;
    public const int BurstLength = 33;

    public static void WriteAmbePayload(Span<byte> burst, ReadOnlySpan<byte> ambe)
    {
        if (burst.Length < BurstLength) throw new ArgumentException("Ein DMR-Burst enthält 33 Byte.", nameof(burst));
        if (ambe.Length < AmbePayloadLength) throw new ArgumentException("Ein Sprachburst enthält drei AMBE-Frames mit zusammen 27 Byte.", nameof(ambe));
        for (var frame = 0; frame < AmbeFramesPerBurst; frame++)
            for (var sourceBit = 0; sourceBit < 72; sourceBit++)
                WriteBit(burst, MapBit(frame, sourceBit), ReadBit(ambe, frame * 72 + sourceBit));
    }

    public static void ReadAmbePayload(ReadOnlySpan<byte> burst, Span<byte> ambe)
    {
        if (burst.Length < BurstLength) throw new ArgumentException("Ein DMR-Burst enthält 33 Byte.", nameof(burst));
        if (ambe.Length < AmbePayloadLength) throw new ArgumentException("Der AMBE-Zielpuffer muss 27 Byte enthalten.", nameof(ambe));
        ambe[..AmbePayloadLength].Clear();
        for (var frame = 0; frame < AmbeFramesPerBurst; frame++)
            for (var targetBit = 0; targetBit < 72; targetBit++)
                WriteBit(ambe, frame * 72 + targetBit, ReadBit(burst, MapBit(frame, targetBit)));
    }

    private static int MapBit(int frame, int canonicalBit)
    {
        int interleaved;
        if (canonicalBit < 24)
            interleaved = canonicalBit < 18 ? canonicalBit * 4 : 1 + (canonicalBit - 18) * 4;
        else if (canonicalBit < 47)
        {
            var bit = canonicalBit - 24;
            interleaved = bit < 12 ? 25 + bit * 4 : 2 + (bit - 12) * 4;
        }
        else
        {
            var bit = canonicalBit - 47;
            interleaved = bit < 7 ? 46 + bit * 4 : 3 + (bit - 7) * 4;
        }

        var position = frame switch
        {
            0 => interleaved,
            1 => 72 + interleaved,
            _ => 192 + interleaved
        };
        if (frame == 1 && position >= 108) position += 48;
        return position;
    }

    private static bool ReadBit(ReadOnlySpan<byte> data, int bit) =>
        (data[bit >> 3] & (0x80 >> (bit & 7))) != 0;

    private static void WriteBit(Span<byte> data, int bit, bool value)
    {
        var mask = (byte)(0x80 >> (bit & 7));
        if (value) data[bit >> 3] |= mask;
        else data[bit >> 3] &= (byte)~mask;
    }
}
