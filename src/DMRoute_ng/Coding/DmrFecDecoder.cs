namespace DMRoute_ng.Coding;

public static class DmrFecDecoder
{
    public static byte[] Decode(ReadOnlySpan<byte> payload, byte colorCode)
    {
        if (payload.Length < 33) return Array.Empty<byte>();
        var result = new byte[12];
        TryDecode(payload, colorCode, result, out _);
        return result;
    }

    public static bool TryDecode(ReadOnlySpan<byte> payload, byte colorCode, Span<byte> destination, out int written)
    {
        if (payload.Length < 33 || destination.Length < 12)
        {
            written = 0;
            return false;
        }

        Span<byte> unmasked = stackalloc byte[33];
        RemoveColorCodeMask(payload[..33], unmasked, colorCode);

        Span<byte> decoded = stackalloc byte[12];
        Bptc19696.Decode(unmasked, decoded);
        decoded.CopyTo(destination);
        written = decoded.Length;
        return true;
    }

    private static void RemoveColorCodeMask(ReadOnlySpan<byte> input, Span<byte> output, byte cc)
    {
        input.CopyTo(output);
    }
    
    private static void RemoveColorCodeMask_(ReadOnlySpan<byte> input, Span<byte> output, byte cc)
    {
        // Die DMR PRBS-Initialisierung verwendet einen festen Seed pro Color Code.
        // Typischerweise wird das Schieberegister mit dem Color Code gefüllt.
        var prbs = (ushort)((cc << 12) | (cc << 8) | (cc << 4) | cc);

        for (var i = 0; i < input.Length; i++)
        {
            byte maskByte = 0;
            for (var b = 7; b >= 0; b--)
            {
                // XOR der relevanten Taps (Bits 15, 13, 12, 10 nach 0-basierter Zählweise)
                var bit = ((prbs >> 15) ^ (prbs >> 13) ^ (prbs >> 12) ^ (prbs >> 10)) & 1;
            
                prbs = (ushort)((prbs << 1) | bit);
                maskByte |= (byte)(bit << b);
            }
            output[i] = (byte)(input[i] ^ maskByte);
        }
    }
}
