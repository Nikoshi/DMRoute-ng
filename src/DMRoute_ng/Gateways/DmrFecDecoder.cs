using System;

namespace DMRoute_ng.Gateways;

public static class DmrFecDecoder
{
    public static byte[] Decode(ReadOnlySpan<byte> payload, byte colorCode)
    {
        if (payload.Length < 33) return Array.Empty<byte>();

        // 1. Color Code De-Masking
        Span<byte> unmasked = stackalloc byte[payload.Length];
        RemoveColorCodeMask(payload, unmasked, colorCode);

        // 3. FEC Decoding (beinhaltet De-Interleaving und Hamming)
        Span<byte> decoded = stackalloc byte[12];
        Bptc19696.Decode(unmasked, decoded);

        return [.. decoded];
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