
namespace DMRoute_ng.Coding;

public static class Bptc19696
{
    private static readonly byte[] BitMasks = [0x80, 0x40, 0x20, 0x10, 0x08, 0x04, 0x02, 0x01];

    public static void Encode(ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (input.Length < 12) throw new ArgumentException("Input must contain 12 bytes.", nameof(input));
        if (output.Length < 33) throw new ArgumentException("Output must contain 33 bytes.", nameof(output));

        Span<bool> deInterleaved = stackalloc bool[196];
        Span<bool> inputBits = stackalloc bool[96];
        for (var i = 0; i < 12; i++) ByteToBitsBE(input[i], inputBits.Slice(i * 8, 8));

        var inputOffset = 0;
        InsertRange(inputBits, deInterleaved, ref inputOffset, 4, 11);
        InsertRange(inputBits, deInterleaved, ref inputOffset, 16, 26);
        InsertRange(inputBits, deInterleaved, ref inputOffset, 31, 41);
        InsertRange(inputBits, deInterleaved, ref inputOffset, 46, 56);
        InsertRange(inputBits, deInterleaved, ref inputOffset, 61, 71);
        InsertRange(inputBits, deInterleaved, ref inputOffset, 76, 86);
        InsertRange(inputBits, deInterleaved, ref inputOffset, 91, 101);
        InsertRange(inputBits, deInterleaved, ref inputOffset, 106, 116);
        InsertRange(inputBits, deInterleaved, ref inputOffset, 121, 131);

        for (var row = 0; row < 9; row++) Hamming.Encode15113_2(deInterleaved.Slice(row * 15 + 1, 15));

        Span<bool> column = stackalloc bool[13];
        for (var col = 0; col < 15; col++)
        {
            var position = col + 1;
            for (var row = 0; row < 13; row++, position += 15) column[row] = deInterleaved[position];
            Hamming.Encode1393(column);
            position = col + 1;
            for (var row = 0; row < 13; row++, position += 15) deInterleaved[position] = column[row];
        }

        Span<bool> raw = stackalloc bool[196];
        for (uint i = 0; i < 196; i++) raw[(int)((i * 181) % 196)] = deInterleaved[(int)i];

        output.Clear();
        for (var i = 0; i < 98; i++) WriteBurstBit(output, i, raw[i]);
        WriteBurstBit(output, 166, raw[98]);
        WriteBurstBit(output, 167, raw[99]);
        for (var i = 100; i < 196; i++) WriteBurstBit(output, i + 68, raw[i]);
    }

    public static void Decode(ReadOnlySpan<byte> input, Span<byte> output)
    {
        Span<bool> rawData = stackalloc bool[196];
        Span<bool> deInterData = stackalloc bool[196];

        DecodeExtractBinary(input, rawData);
        DecodeDeInterleave(rawData, deInterData);
        DecodeErrorCheck(deInterData);
        DecodeExtractData(deInterData, output);
    }

    private static void DecodeExtractBinary(ReadOnlySpan<byte> input, Span<bool> rawData)
    {
        for (var i = 0; i < 13; i++)
            ByteToBitsBE(input[i], rawData.Slice(i * 8, 8));

        Span<bool> bits = stackalloc bool[8];
        ByteToBitsBE(input[20], bits);
        rawData[98] = bits[6];
        rawData[99] = bits[7];

        for (var i = 0; i < 12; i++)
            ByteToBitsBE(input[21 + i], rawData.Slice(100 + (i * 8), 8));
    }

    private static void DecodeDeInterleave(ReadOnlySpan<bool> rawData, Span<bool> deInterData)
    {
        deInterData.Clear();
        for (uint a = 0; a < 196; a++)
        {
            var interleaveSequence = (a * 181) % 196;
            deInterData[(int)a] = rawData[(int)interleaveSequence];
        }
    }

    // ReSharper disable once CognitiveComplexity
    private static void DecodeErrorCheck(Span<bool> deInterData)
    {
        bool fixing;
        uint count = 0;
        Span<bool> col = stackalloc bool[13];

        do
        {
            fixing = false;

            for (var c = 0; c < 15; c++)
            {
                var pos = c + 1;
                for (var a = 0; a < 13; a++)
                {
                    col[a] = deInterData[pos];
                    pos += 15;
                }

                if (Hamming.Decode1393(col))
                {
                    pos = c + 1;
                    for (int a = 0; a < 13; a++)
                    {
                        deInterData[pos] = col[a];
                        pos += 15;
                    }
                    fixing = true;
                }
            }

            for (var r = 0; r < 9; r++)
            {
                var pos = (r * 15) + 1;
                if (Hamming.Decode15113_2(deInterData.Slice(pos)))
                    fixing = true;
            }

            count++;
        } while (fixing && count < 5);
    }
    
    private static void DecodeExtractData(ReadOnlySpan<bool> deInterData, Span<byte> data)
    {
        Span<bool> bData = stackalloc bool[96];
        var pos = 0;

        ExtractRange(deInterData, bData, ref pos, 4, 11);
        ExtractRange(deInterData, bData, ref pos, 16, 26);
        ExtractRange(deInterData, bData, ref pos, 31, 41);
        ExtractRange(deInterData, bData, ref pos, 46, 56);
        ExtractRange(deInterData, bData, ref pos, 61, 71);
        ExtractRange(deInterData, bData, ref pos, 76, 86);
        ExtractRange(deInterData, bData, ref pos, 91, 101);
        ExtractRange(deInterData, bData, ref pos, 106, 116);
        ExtractRange(deInterData, bData, ref pos, 121, 131);

        for (var i = 0; i < 12; i++)
            data[i] = BitsToByteBE(bData.Slice(i * 8, 8));
    }
    
    private static void ExtractRange(ReadOnlySpan<bool> source, Span<bool> target, ref int targetPos, int start, int end)
    {
        for (var a = start; a <= end; a++)
            target[targetPos++] = source[a];
    }

    private static void InsertRange(ReadOnlySpan<bool> source, Span<bool> target, ref int sourcePos, int start, int end)
    {
        for (var targetPos = start; targetPos <= end; targetPos++) target[targetPos] = source[sourcePos++];
    }

    private static void WriteBurstBit(Span<byte> output, int position, bool value)
    {
        if (value) output[position >> 3] |= BitMasks[position & 7];
    }

    // ReSharper disable once InconsistentNaming
    private static void ByteToBitsBE(byte b, Span<bool> bits)
    {
        bits[0] = (b & 0x80) != 0;
        bits[1] = (b & 0x40) != 0;
        bits[2] = (b & 0x20) != 0;
        bits[3] = (b & 0x10) != 0;
        bits[4] = (b & 0x08) != 0;
        bits[5] = (b & 0x04) != 0;
        bits[6] = (b & 0x02) != 0;
        bits[7] = (b & 0x01) != 0;
    }

    // ReSharper disable once InconsistentNaming
    private static byte BitsToByteBE(ReadOnlySpan<bool> bits)
    {
        byte b = 0;
        if (bits[0]) b |= 0x80;
        if (bits[1]) b |= 0x40;
        if (bits[2]) b |= 0x20;
        if (bits[3]) b |= 0x10;
        if (bits[4]) b |= 0x08;
        if (bits[5]) b |= 0x04;
        if (bits[6]) b |= 0x02;
        if (bits[7]) b |= 0x01;
        return b;
    }
}
