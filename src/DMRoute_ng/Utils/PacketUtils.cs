using System.Buffers.Binary;

namespace DMRoute_ng.Utils;

public static class PacketUtils
{
    public const int RptAckLength = 10;
    public const int MstcLength = 12;
    public const int MstaLength = 8;
    public const int MstNakLength = 10;
    public const int MstPongLength = 11;

    // HB Protocol
    public static ReadOnlySpan<byte> RptlHeader => "RPTL"u8;
    public static ReadOnlySpan<byte> RptkHeader => "RPTK"u8;
    public static ReadOnlySpan<byte> RptPingHeader => "RPTPING"u8;
    public static ReadOnlySpan<byte> DmrdHeader => "DMRD"u8;
    public static ReadOnlySpan<byte> DmrcHeader => "DMRC"u8;
    public static ReadOnlySpan<byte> RptcHeader => "RPTC"u8;
    
    // Master Discovery Protocol
    public static ReadOnlySpan<byte> DmbdHeader => "DMBD"u8; // DMR Broadcast Discovery
    public static ReadOnlySpan<byte> DmbcHeader => "DMBC"u8; // DMR Broadcast Challenge Response
    
    // Master Challenge (Antwort auf RPTL) im korrekten RPTACK-Format
    public static byte[] BuildRptAck(uint salt)
    {
        var packet = new byte[RptAckLength];
        TryWriteRptAck(packet, salt, out _);
        return packet;
    }

    public static bool TryWriteRptAck(Span<byte> destination, uint salt, out int written)
    {
        if (destination.Length < RptAckLength) { written = 0; return false; }
        "RPTACK"u8.CopyTo(destination);
        BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(6, 4), salt);
        written = RptAckLength;
        return true;
    }
    
    public static byte[] BuildMstc(int repeaterId, uint salt)
    {
        var packet = new byte[MstcLength];
        TryWriteMstc(packet, repeaterId, salt, out _);
        return packet;
    }

    public static bool TryWriteMstc(Span<byte> destination, int repeaterId, uint salt, out int written)
    {
        if (destination.Length < MstcLength) { written = 0; return false; }
        "MSTC"u8.CopyTo(destination);
        BinaryPrimitives.WriteInt32BigEndian(destination.Slice(4, 4), repeaterId);
        BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(8, 4), salt);
        written = MstcLength;
        return true;
    }

    // Master Ack (Antwort auf erfolgreichen RPTK)
    public static byte[] BuildMsta(int repeaterId)
    {
        var packet = new byte[MstaLength];
        TryWriteMsta(packet, repeaterId, out _);
        return packet;
    }

    public static bool TryWriteMsta(Span<byte> destination, int repeaterId, out int written)
    {
        if (destination.Length < MstaLength) { written = 0; return false; }
        "MSTA"u8.CopyTo(destination);
        BinaryPrimitives.WriteInt32BigEndian(destination.Slice(4, 4), repeaterId);
        written = MstaLength;
        return true;
    }

    // Master NAK (Passwort falsch oder Repeater unbekannt)
    public static byte[] BuildMstNak(int repeaterId)
    {
        var packet = new byte[MstNakLength];
        TryWriteMstNak(packet, repeaterId, out _);
        return packet;
    }

    public static bool TryWriteMstNak(Span<byte> destination, int repeaterId, out int written)
    {
        if (destination.Length < MstNakLength) { written = 0; return false; }
        "MSTNAK"u8.CopyTo(destination);
        BinaryPrimitives.WriteInt32BigEndian(destination.Slice(6, 4), repeaterId);
        written = MstNakLength;
        return true;
    }

    // Master Pong (Antwort auf RPTPING)
    public static byte[] BuildMstPong(int repeaterId)
    {
        var packet = new byte[MstPongLength];
        TryWriteMstPong(packet, repeaterId, out _);
        return packet;
    }

    public static bool TryWriteMstPong(Span<byte> destination, int repeaterId, out int written)
    {
        if (destination.Length < MstPongLength) { written = 0; return false; }
        "MSTPONG"u8.CopyTo(destination);
        BinaryPrimitives.WriteInt32BigEndian(destination.Slice(7, 4), repeaterId);
        written = MstPongLength;
        return true;
    }
}
