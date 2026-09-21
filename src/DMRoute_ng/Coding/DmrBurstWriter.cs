using System.Buffers.Binary;

namespace DMRoute_ng.Coding;

public static class DmrBurstWriter
{
    public const int BurstLength = 33;
    public const int DmrdLength = 53;
    private const ushort CsbkMask = 0xA5A5;
    private const ushort DataHeaderMask = 0xCCCC;
    private const ulong MobileStationDataSync = 0xD5D7F77FD757UL;

    public static void WriteCsbkPreamble(
        Span<byte> destination, int sourceId, int destinationId, byte blocksToFollow, byte colorCode = 1)
    {
        Span<byte> decoded = stackalloc byte[12];
        decoded[0] = 0xBD;
        decoded[1] = 0x00;
        decoded[2] = 0x80;
        decoded[3] = blocksToFollow;
        WriteId(decoded[4..7], destinationId);
        WriteId(decoded[7..10], sourceId);
        DmrCrc.WriteMaskedCcitt(decoded[10..12], decoded[..10], CsbkMask);
        Bptc19696.Encode(decoded, destination);
        WriteSlotTypeAndSync(destination, colorCode, 0x03);
    }

    public static void WriteConfirmedDataHeader(
        Span<byte> destination,
        int sourceId,
        int destinationId,
        byte blocksToFollow,
        byte padOctets,
        byte sendSequence,
        byte colorCode = 1)
    {
        Span<byte> decoded = stackalloc byte[12];
        decoded[0] = 0x43; // Response requested, individual call, confirmed packet data.
        decoded[1] = (byte)(0x40 | (padOctets & 0x0F)); // IP SAP plus pad count.
        WriteId(decoded[2..5], destinationId);
        WriteId(decoded[5..8], sourceId);
        decoded[8] = (byte)(0x80 | blocksToFollow);
        decoded[9] = (byte)(((sendSequence & 0x07) << 4) | 0x08);
        DmrCrc.WriteMaskedCcitt(decoded[10..12], decoded[..10], DataHeaderMask);
        Bptc19696.Encode(decoded, destination);
        WriteSlotTypeAndSync(destination, colorCode, 0x06);
    }

    public static void WriteUnconfirmedDataHeader(
        Span<byte> destination,
        int sourceId,
        int destinationId,
        byte blocksToFollow,
        byte padOctets,
        bool isGroupCall,
        byte colorCode = 1)
    {
        Span<byte> decoded = stackalloc byte[12];
        if (padOctets > 31) throw new ArgumentOutOfRangeException(nameof(padOctets));
        decoded[0] = (byte)((isGroupCall ? 0x80 : 0x00) |
                            ((padOctets & 0x10) != 0 ? 0x10 : 0x00) |
                            0x02); // GI, high pad bit and unconfirmed packet data.
        decoded[1] = (byte)(0x40 | (padOctets & 0x0F)); // IP SAP plus pad count.
        WriteId(decoded[2..5], destinationId);
        WriteId(decoded[5..8], sourceId);
        decoded[8] = blocksToFollow;
        decoded[9] = 0;
        DmrCrc.WriteMaskedCcitt(decoded[10..12], decoded[..10], DataHeaderMask);
        Bptc19696.Encode(decoded, destination);
        WriteSlotTypeAndSync(destination, colorCode, 0x06);
    }

    public static void WriteConfirmedRateThreeQuarterBlock(
        Span<byte> destination, ReadOnlySpan<byte> data, byte serialNumber, byte colorCode = 1)
    {
        if (data.Length != 16) throw new ArgumentException("A confirmed rate-3/4 block contains 16 data bytes.", nameof(data));
        Span<byte> decoded = stackalloc byte[18];
        var crc = DmrCrc.ConfirmedBlockCrc9(data, serialNumber);
        decoded[0] = (byte)(((serialNumber & 0x7F) << 1) | (crc >> 8));
        decoded[1] = (byte)crc;
        data.CopyTo(decoded[2..]);
        DmrTrellis.Encode(decoded, destination);
        WriteSlotTypeAndSync(destination, colorCode, 0x08);
    }

    public static void WriteUnconfirmedRateThreeQuarterBlock(
        Span<byte> destination, ReadOnlySpan<byte> data, byte colorCode = 1)
    {
        if (data.Length != 18) throw new ArgumentException("An unconfirmed rate-3/4 block contains 18 data bytes.", nameof(data));
        DmrTrellis.Encode(data, destination);
        WriteSlotTypeAndSync(destination, colorCode, 0x08);
    }

    public static void WriteConfirmedResponseHeader(
        Span<byte> destination,
        int sourceId,
        int destinationId,
        byte sendSequence,
        bool acknowledged,
        byte colorCode = 1)
    {
        Span<byte> decoded = stackalloc byte[12];
        decoded[0] = 0x01;
        decoded[1] = 0x40;
        WriteId(decoded[2..5], destinationId);
        WriteId(decoded[5..8], sourceId);
        decoded[8] = acknowledged ? (byte)0x00 : (byte)0x20;
        decoded[9] = (byte)(((sendSequence & 0x07) << 4) | 0x08);
        DmrCrc.WriteMaskedCcitt(decoded[10..12], decoded[..10], DataHeaderMask);
        Bptc19696.Encode(decoded, destination);
        WriteSlotTypeAndSync(destination, colorCode, 0x06);
    }

    public static bool TryReadConfirmedResponse(
        ReadOnlySpan<byte> dmrd, int expectedSourceId, int expectedDestinationId, out bool acknowledged)
    {
        acknowledged = false;
        if (dmrd.Length < DmrdLength || (dmrd[15] & 0x2F) != 0x26) return false;
        var sourceId = ReadId(dmrd[5..8]);
        var destinationId = ReadId(dmrd[8..11]);
        if (sourceId != expectedSourceId || destinationId != expectedDestinationId) return false;

        Span<byte> decoded = stackalloc byte[12];
        Bptc19696.Decode(dmrd[20..53], decoded);
        if ((decoded[0] & 0x0F) != 0x01 || !DmrCrc.CheckMaskedCcitt(decoded, DataHeaderMask)) return false;
        acknowledged = decoded[8] == 0;
        return true;
    }

    public static void WriteDmrd(
        Span<byte> destination,
        ReadOnlySpan<byte> burst,
        byte sequence,
        int sourceId,
        int destinationId,
        int repeaterId,
        byte dataType,
        uint streamId,
        int timeslot = 2,
        bool isGroupCall = false)
    {
        if (destination.Length < DmrdLength) throw new ArgumentException("DMRD output must contain 53 bytes.", nameof(destination));
        if (burst.Length < BurstLength) throw new ArgumentException("A DMR burst contains 33 bytes.", nameof(burst));
        "DMRD"u8.CopyTo(destination);
        destination[4] = sequence;
        WriteId(destination[5..8], sourceId);
        WriteId(destination[8..11], destinationId);
        BinaryPrimitives.WriteInt32BigEndian(destination[11..15], repeaterId);
        if (timeslot is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(timeslot));
        destination[15] = (byte)(0x20 |
                                 (timeslot == 2 ? 0x80 : 0x00) |
                                 (isGroupCall ? 0x00 : 0x40) |
                                 (dataType & 0x0F));
        BinaryPrimitives.WriteUInt32BigEndian(destination[16..20], streamId);
        burst[..BurstLength].CopyTo(destination[20..53]);
    }

    private static void WriteSlotTypeAndSync(Span<byte> burst, byte colorCode, byte dataType)
    {
        if (burst.Length < BurstLength) throw new ArgumentException("A DMR burst contains 33 bytes.", nameof(burst));
        Span<byte> slotType = stackalloc byte[3];
        slotType[0] = (byte)(((colorCode & 0x0F) << 4) | (dataType & 0x0F));
        Golay2087.Encode(slotType);

        for (var bit = 0; bit < 20; bit++)
        {
            bool value;
            if (bit < 8) value = (slotType[0] & (0x80 >> bit)) != 0;
            else if (bit < 16) value = (slotType[1] & (0x80 >> (bit - 8))) != 0;
            else value = (slotType[2] & (0x80 >> (bit - 16))) != 0;
            WriteBit(burst, bit < 10 ? 98 + bit : 146 + bit, value);
        }

        for (var bit = 0; bit < 48; bit++)
            WriteBit(burst, 108 + bit, (MobileStationDataSync & (1UL << (47 - bit))) != 0);
    }

    private static int ReadId(ReadOnlySpan<byte> data) => (data[0] << 16) | (data[1] << 8) | data[2];

    private static void WriteId(Span<byte> destination, int id)
    {
        destination[0] = (byte)(id >> 16);
        destination[1] = (byte)(id >> 8);
        destination[2] = (byte)id;
    }

    private static void WriteBit(Span<byte> data, int position, bool value)
    {
        var mask = (byte)(0x80 >> (position & 7));
        if (value) data[position >> 3] |= mask;
        else data[position >> 3] &= (byte)~mask;
    }
}
