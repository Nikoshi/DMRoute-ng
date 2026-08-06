using System;

namespace DMRoute_ng.Gateways;

public static class DmrFecDecoder
{
    public static byte[] Decode(ReadOnlySpan<byte> payload, byte colorCode)
    {
        if (payload.Length < 33) return Array.Empty<byte>();

        // 1. Color Code De-Masking
        // Die 33 Bytes werden mit einer Pseudo-Zufallssequenz (basierend auf dem Color Code) XOR-maskiert.
        Span<byte> unmasked = stackalloc byte[payload.Length];
        RemoveColorCodeMask(payload, unmasked, colorCode);

        // 2. De-Interleaving
        // Die Bits sind über den gesamten Block verwürfelt, um gegen Burst-Fehler resistent zu sein.
        // Sie müssen in ihre ursprüngliche Reihenfolge (Matrix-Transposition) zurückgeschoben werden.
        Span<byte> deinterleaved = stackalloc byte[payload.Length];
        DeInterleave(unmasked, deinterleaved);

        // 3. FEC Decoding (Fehlerkorrektur)
        // Bei Datenblöcken kommt meist BPTC (196, 96) zum Einsatz. 
        // 196 Bits Eingabe -> 96 Bits (12 Bytes) extrahierte, fehlerkorrigierte Nutzdaten.
        return ApplyBptcFec(deinterleaved);
    }

    private static void RemoveColorCodeMask(ReadOnlySpan<byte> input, Span<byte> output, byte cc)
    {
        // TODO: ETSI Pseudo-Random-Sequence Generator für den Color Code implementieren.
        // input.CopyTo(output); (Vorerst unmaskiert weitergeben)
    }

    private static void DeInterleave(ReadOnlySpan<byte> input, Span<byte> output)
    {
        // TODO: Bit-weises Verschieben gemäß der ETSI Interleaving-Tabelle.
    }

    private static byte[] ApplyBptcFec(ReadOnlySpan<byte> input)
    {
        // TODO: BPTC Matrix-Multiplikation.
        throw new NotImplementedException("FEC Decoding erfordert C++ Portierung oder P/Invoke.");
    }
}