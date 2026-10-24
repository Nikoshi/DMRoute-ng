using System.Buffers.Binary;

namespace DMRoute_ng.Utils;

/// <summary>
/// Kodiert die festen Homebrew-Anmelde- und Keepalive-Pakete des Masters.
/// </summary>
/// <remarks>
/// Die <c>TryWrite*</c>-Varianten sind für Hot Paths vorgesehen und schreiben in
/// aufruferverwaltete Puffer. Die <c>Build*</c>-Varianten sind komfortable,
/// allokierende Wrapper für Start- und Testpfade.
/// </remarks>
public static class PacketUtils
{
    public const int RptAckLength = 10;
    public const int MstcLength = 12;
    public const int MstaLength = 8;
    public const int MstNakLength = 10;
    public const int MstPongLength = 11;

    // Homebrew-Protokollkennungen, wie sie unverändert auf dem UDP-Wire stehen.
    public static ReadOnlySpan<byte> RptlHeader => "RPTL"u8;
    public static ReadOnlySpan<byte> RptkHeader => "RPTK"u8;
    public static ReadOnlySpan<byte> RptPingHeader => "RPTPING"u8;
    public static ReadOnlySpan<byte> DmrdHeader => "DMRD"u8;
    public static ReadOnlySpan<byte> DmrcHeader => "DMRC"u8;
    public static ReadOnlySpan<byte> RptcHeader => "RPTC"u8;

    // DMRoute-ng-Master-Discovery: Broadcast Discovery und Challenge Response.
    public static ReadOnlySpan<byte> DmbdHeader => "DMBD"u8;
    public static ReadOnlySpan<byte> DmbcHeader => "DMBC"u8;

    /// <summary>Erzeugt die RPTACK-Challenge als neues Array.</summary>
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

    /// <summary>Erzeugt die Bestätigung einer erfolgreichen RPTK-Anmeldung.</summary>
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

    /// <summary>Erzeugt die Ablehnung einer fehlgeschlagenen Anmeldung.</summary>
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

    /// <summary>Erzeugt die Antwort auf ein RPTPING.</summary>
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
