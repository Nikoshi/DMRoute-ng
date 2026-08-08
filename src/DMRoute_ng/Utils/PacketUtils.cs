using System.Buffers.Binary;

namespace DMRoute_ng.Utils;

public static class PacketUtils
{
    // HB Protocol
    public static ReadOnlySpan<byte> RptlHeader => "RPTL"u8;
    public static ReadOnlySpan<byte> RptkHeader => "RPTK"u8;
    public static ReadOnlySpan<byte> RptPingHeader => "RPTPING"u8;
    public static ReadOnlySpan<byte> DmrdHeader => "DMRD"u8;
    public static ReadOnlySpan<byte> RptcHeader => "RPTC"u8;
    
    // Master Discovery Protocol
    public static ReadOnlySpan<byte> DmbdHeader => "DMBD"u8; // DMR Broadcast Discovery
    public static ReadOnlySpan<byte> DmbcHeader => "DMBC"u8; // DMR Broadcast Challenge Response
    
    // Master Challenge (Antwort auf RPTL) im korrekten RPTACK-Format
    public static byte[] BuildRptAck(uint salt)
    {
        var packet = new byte[10]; // 6 Bytes "RPTACK" + 4 Bytes Salt
        "RPTACK"u8.CopyTo(packet.AsSpan(0, 6));
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(6, 4), salt);
        return packet;
    }
    
    public static byte[] BuildMstc(int repeaterId, uint salt)
    {
        var packet = new byte[12];
        "MSTC"u8.CopyTo(packet.AsSpan(0, 4));
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(4, 4), repeaterId);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8, 4), salt);
        return packet;
    }

    // Master Ack (Antwort auf erfolgreichen RPTK)
    public static byte[] BuildMsta(int repeaterId)
    {
        var packet = new byte[8];
        "MSTA"u8.CopyTo(packet.AsSpan(0, 4));
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(4, 4), repeaterId);
        return packet;
    }

    // Master NAK (Passwort falsch oder Repeater unbekannt)
    public static byte[] BuildMstNak(int repeaterId)
    {
        var packet = new byte[10];
        "MSTNAK"u8.CopyTo(packet.AsSpan(0, 6));
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(6, 4), repeaterId);
        return packet;
    }

    // Master Pong (Antwort auf RPTPING)
    public static byte[] BuildMstPong(int repeaterId)
    {
        var packet = new byte[11];
        "MSTPONG"u8.CopyTo(packet.AsSpan(0, 7));
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(7, 4), repeaterId);
        return packet;
    }
}