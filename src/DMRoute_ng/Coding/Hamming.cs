namespace DMRoute_ng.Coding;

public static class Hamming
{
    public static bool Decode15113_2(Span<bool> d)
    {
        bool c0 = d[0] ^ d[1] ^ d[2] ^ d[3] ^ d[5] ^ d[7] ^ d[8];
        bool c1 = d[1] ^ d[2] ^ d[3] ^ d[4] ^ d[6] ^ d[8] ^ d[9];
        bool c2 = d[2] ^ d[3] ^ d[4] ^ d[5] ^ d[7] ^ d[9] ^ d[10];
        bool c3 = d[0] ^ d[1] ^ d[2] ^ d[4] ^ d[6] ^ d[7] ^ d[10];

        byte n = 0x00;
        if (c0 != d[11]) n |= 0x01;
        if (c1 != d[12]) n |= 0x02;
        if (c2 != d[13]) n |= 0x04;
        if (c3 != d[14]) n |= 0x08;

        switch (n)
        {
            case 0x01: d[11] = !d[11]; return true;
            case 0x02: d[12] = !d[12]; return true;
            case 0x04: d[13] = !d[13]; return true;
            case 0x08: d[14] = !d[14]; return true;
            case 0x09: d[0]  = !d[0];  return true;
            case 0x0B: d[1]  = !d[1];  return true;
            case 0x0F: d[2]  = !d[2];  return true;
            case 0x07: d[3]  = !d[3];  return true;
            case 0x0E: d[4]  = !d[4];  return true;
            case 0x05: d[5]  = !d[5];  return true;
            case 0x0A: d[6]  = !d[6];  return true;
            case 0x0D: d[7]  = !d[7];  return true;
            case 0x03: d[8]  = !d[8];  return true;
            case 0x06: d[9]  = !d[9];  return true;
            case 0x0C: d[10] = !d[10]; return true;
            default: return false;
        }
    }

    public static void Encode15113_2(Span<bool> d)
    {
        d[11] = d[0] ^ d[1] ^ d[2] ^ d[3] ^ d[5] ^ d[7] ^ d[8];
        d[12] = d[1] ^ d[2] ^ d[3] ^ d[4] ^ d[6] ^ d[8] ^ d[9];
        d[13] = d[2] ^ d[3] ^ d[4] ^ d[5] ^ d[7] ^ d[9] ^ d[10];
        d[14] = d[0] ^ d[1] ^ d[2] ^ d[4] ^ d[6] ^ d[7] ^ d[10];
    }

    public static bool Decode1393(Span<bool> d)
    {
        bool c0 = d[0] ^ d[1] ^ d[3] ^ d[5] ^ d[6];
        bool c1 = d[0] ^ d[1] ^ d[2] ^ d[4] ^ d[6] ^ d[7];
        bool c2 = d[0] ^ d[1] ^ d[2] ^ d[3] ^ d[5] ^ d[7] ^ d[8];
        bool c3 = d[0] ^ d[2] ^ d[4] ^ d[5] ^ d[8];

        byte n = 0x00;
        if (c0 != d[9])  n |= 0x01;
        if (c1 != d[10]) n |= 0x02;
        if (c2 != d[11]) n |= 0x04;
        if (c3 != d[12]) n |= 0x08;

        switch (n)
        {
            case 0x01: d[9]  = !d[9];  return true;
            case 0x02: d[10] = !d[10]; return true;
            case 0x04: d[11] = !d[11]; return true;
            case 0x08: d[12] = !d[12]; return true;
            case 0x0F: d[0]  = !d[0];  return true;
            case 0x07: d[1]  = !d[1];  return true;
            case 0x0E: d[2]  = !d[2];  return true;
            case 0x05: d[3]  = !d[3];  return true;
            case 0x0A: d[4]  = !d[4];  return true;
            case 0x0D: d[5]  = !d[5];  return true;
            case 0x03: d[6]  = !d[6];  return true;
            case 0x06: d[7]  = !d[7];  return true;
            case 0x0C: d[8]  = !d[8];  return true;
            default: return false;
        }
    }

    public static void Encode1393(Span<bool> d)
    {
        d[9]  = d[0] ^ d[1] ^ d[3] ^ d[5] ^ d[6];
        d[10] = d[0] ^ d[1] ^ d[2] ^ d[4] ^ d[6] ^ d[7];
        d[11] = d[0] ^ d[1] ^ d[2] ^ d[3] ^ d[5] ^ d[7] ^ d[8];
        d[12] = d[0] ^ d[2] ^ d[4] ^ d[5] ^ d[8];
    }
}