namespace DMRoute_ng.Coding;

public static class DmrTrellis
{
    private static readonly uint[] InterleaveTable = {
        0, 1, 8, 9, 16, 17, 24, 25, 32, 33, 40, 41, 48, 49, 56, 57, 64, 65, 72, 73, 80, 81, 88, 89, 96, 97,
        2, 3, 10, 11, 18, 19, 26, 27, 34, 35, 42, 43, 50, 51, 58, 59, 66, 67, 74, 75, 82, 83, 90, 91,
        4, 5, 12, 13, 20, 21, 28, 29, 36, 37, 44, 45, 52, 53, 60, 61, 68, 69, 76, 77, 84, 85, 92, 93,
        6, 7, 14, 15, 22, 23, 30, 31, 38, 39, 46, 47, 54, 55, 62, 63, 70, 71, 78, 79, 86, 87, 94, 95
    };

    private static readonly byte[] EncodeTable = {
        0, 8, 4, 12, 2, 10, 6, 14,
        4, 12, 2, 10, 6, 14, 0, 8,
        1, 9, 5, 13, 3, 11, 7, 15,
        5, 13, 3, 11, 7, 15, 1, 9,
        3, 11, 7, 15, 1, 9, 5, 13,
        7, 15, 1, 9, 5, 13, 3, 11,
        2, 10, 6, 14, 0, 8, 4, 12,
        6, 14, 0, 8, 4, 12, 2, 10
    };

    private static readonly byte[] BitMaskTable = { 0x80, 0x40, 0x20, 0x10, 0x08, 0x04, 0x02, 0x01 };

    private static bool ReadBit(ReadOnlySpan<byte> p, int i) => (p[i >> 3] & BitMaskTable[i & 7]) != 0;
    private static void WriteBit(Span<byte> p, int i, bool b)
    {
        if (b) p[i >> 3] |= BitMaskTable[i & 7];
        else p[i >> 3] &= (byte)~BitMaskTable[i & 7];
    }

    public static bool Decode(ReadOnlySpan<byte> data, Span<byte> payload)
    {
        Span<sbyte> dibits = stackalloc sbyte[98];
        Deinterleave(data, dibits);

        Span<byte> points = stackalloc byte[49];
        DibitsToPoints(dibits, points);

        Span<byte> tribits = stackalloc byte[49];
        uint failPos = CheckCode(points, tribits);
        
        if (failPos == 999)
        {
            TribitsToBits(tribits, payload);
            return true;
        }

        Span<byte> savePoints = stackalloc byte[49];
        points.CopyTo(savePoints);

        if (FixCode(points, failPos, payload))
            return true;

        if (failPos == 0)
            return false;

        return FixCode(savePoints, failPos - 1, payload);
    }

    private static void Deinterleave(ReadOnlySpan<byte> data, Span<sbyte> dibits)
    {
        for (int i = 0; i < 98; i++)
        {
            int n = i * 2;
            if (n >= 98) n += 68;
            bool b1 = ReadBit(data, n);

            n = i * 2 + 1;
            if (n >= 98) n += 68;
            bool b2 = ReadBit(data, n);

            sbyte dibit;
            if (!b1 && b2) dibit = 3;
            else if (!b1 && !b2) dibit = 1;
            else if (b1 && !b2) dibit = -1;
            else dibit = -3;

            dibits[(int)InterleaveTable[i]] = dibit;
        }
    }

    private static void DibitsToPoints(ReadOnlySpan<sbyte> dibits, Span<byte> points)
    {
        for (int i = 0; i < 49; i++)
        {
            sbyte d0 = dibits[i * 2];
            sbyte d1 = dibits[i * 2 + 1];

            if (d0 == 1 && d1 == -1) points[i] = 0;
            else if (d0 == -1 && d1 == -1) points[i] = 1;
            else if (d0 == 3 && d1 == -3) points[i] = 2;
            else if (d0 == -3 && d1 == -3) points[i] = 3;
            else if (d0 == -3 && d1 == -1) points[i] = 4;
            else if (d0 == 3 && d1 == -1) points[i] = 5;
            else if (d0 == -1 && d1 == -3) points[i] = 6;
            else if (d0 == 1 && d1 == -3) points[i] = 7;
            else if (d0 == -3 && d1 == 3) points[i] = 8;
            else if (d0 == 3 && d1 == 3) points[i] = 9;
            else if (d0 == -1 && d1 == 1) points[i] = 10;
            else if (d0 == 1 && d1 == 1) points[i] = 11;
            else if (d0 == 1 && d1 == 3) points[i] = 12;
            else if (d0 == -1 && d1 == 3) points[i] = 13;
            else if (d0 == 3 && d1 == 1) points[i] = 14;
            else points[i] = 15;
        }
    }

    private static void TribitsToBits(ReadOnlySpan<byte> tribits, Span<byte> payload)
    {
        for (int i = 0; i < 48; i++)
        {
            byte tribit = tribits[i];
            bool b1 = (tribit & 0x04) == 0x04;
            bool b2 = (tribit & 0x02) == 0x02;
            bool b3 = (tribit & 0x01) == 0x01;

            int n = i * 3;
            WriteBit(payload, n, b1);
            WriteBit(payload, n + 1, b2);
            WriteBit(payload, n + 2, b3);
        }
    }

    private static bool FixCode(Span<byte> points, uint failPos, Span<byte> payload)
    {
        Span<byte> tribits = stackalloc byte[49];

        for (int j = 0; j < 20; j++)
        {
            uint bestPos = 0;
            byte bestVal = 0;

            for (byte i = 0; i < 16; i++)
            {
                points[(int)failPos] = i;
                uint pos = CheckCode(points, tribits);
                
                if (pos == 999)
                {
                    TribitsToBits(tribits, payload);
                    return true;
                }

                if (pos > bestPos)
                {
                    bestPos = pos;
                    bestVal = i;
                }
            }

            points[(int)failPos] = bestVal;
            failPos = bestPos;
        }
        return false;
    }

    private static uint CheckCode(ReadOnlySpan<byte> points, Span<byte> tribits)
    {
        byte state = 0;

        for (int i = 0; i < 49; i++)
        {
            tribits[i] = 9;

            for (byte j = 0; j < 8; j++)
            {
                if (points[i] == EncodeTable[state * 8 + j])
                {
                    tribits[i] = j;
                    break;
                }
            }

            if (tribits[i] == 9) return (uint)i;
            state = tribits[i];
        }

        if (tribits[48] != 0) return 48;
        return 999;
    }
}