using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DMRoute_ng.Emulator;

public static class HomebrewProtocol
{
    public const int RptlLength = 8;
    public const int RptkLength = 40;
    public const int RptcLength = 302;
    public const int RptPingLength = 11;
    public const int RptCloseLength = 9;
    public const int RptAckLength = 10;
    public const int MstNakLength = 10;
    public const int MstPongLength = 11;
    public const int MinimumDmrdLength = 23;

    public static byte[] CreateRptl(int repeaterId)
    {
        var packet = new byte[RptlLength];
        "RPTL"u8.CopyTo(packet);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(4), repeaterId);
        return packet;
    }

    public static byte[] CreateRptk(int repeaterId, uint salt, string preSharedKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(preSharedKey);
        EnsureAscii(preSharedKey, nameof(preSharedKey));

        var pskLength = Encoding.ASCII.GetByteCount(preSharedKey);
        var hashInput = new byte[4 + pskLength];
        BinaryPrimitives.WriteUInt32BigEndian(hashInput, salt);
        Encoding.ASCII.GetBytes(preSharedKey, hashInput.AsSpan(4));

        var packet = new byte[RptkLength];
        "RPTK"u8.CopyTo(packet);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(4), repeaterId);
        SHA256.HashData(hashInput, packet.AsSpan(8));
        CryptographicOperations.ZeroMemory(hashInput);
        return packet;
    }

    public static byte[] CreateRptc(int repeaterId, HotspotConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.TxPower is < 0 or > 99)
            throw new ArgumentOutOfRangeException(nameof(configuration), "TxPower must fit the two-byte RPTC field.");
        if (configuration.ColorCode is < 0 or > 99)
            throw new ArgumentOutOfRangeException(nameof(configuration), "ColorCode must fit the two-byte RPTC field.");
        if (configuration.Height is < 0 or > 999)
            throw new ArgumentOutOfRangeException(nameof(configuration), "Height must fit the three-byte RPTC field.");

        var packet = new byte[RptcLength];
        packet.AsSpan().Fill((byte)' ');
        "RPTC"u8.CopyTo(packet);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(4), repeaterId);

        var payload = packet.AsSpan(8);
        WriteFixedAscii(ref payload, configuration.Callsign, 8, nameof(configuration.Callsign));
        WriteFixedAscii(ref payload, configuration.RxFrequency, 9, nameof(configuration.RxFrequency));
        WriteFixedAscii(ref payload, configuration.TxFrequency, 9, nameof(configuration.TxFrequency));
        WriteFixedAscii(ref payload, configuration.TxPower.ToString("D2", CultureInfo.InvariantCulture), 2, nameof(configuration.TxPower));
        WriteFixedAscii(ref payload, configuration.ColorCode.ToString("D2", CultureInfo.InvariantCulture), 2, nameof(configuration.ColorCode));
        WriteFixedAscii(ref payload, configuration.Latitude, 8, nameof(configuration.Latitude));
        WriteFixedAscii(ref payload, configuration.Longitude, 9, nameof(configuration.Longitude));
        WriteFixedAscii(ref payload, configuration.Height.ToString("D3", CultureInfo.InvariantCulture), 3, nameof(configuration.Height));
        WriteFixedAscii(ref payload, configuration.Location, 20, nameof(configuration.Location));
        WriteFixedAscii(ref payload, configuration.Description, 20, nameof(configuration.Description));
        WriteFixedAscii(ref payload, configuration.Url, 124, nameof(configuration.Url));
        WriteFixedAscii(ref payload, configuration.SoftwareId, 40, nameof(configuration.SoftwareId));
        WriteFixedAscii(ref payload, configuration.PackageId, 40, nameof(configuration.PackageId));
        return packet;
    }

    public static byte[] CreateRptPing(int repeaterId)
    {
        var packet = new byte[RptPingLength];
        "RPTPING"u8.CopyTo(packet);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(7), repeaterId);
        return packet;
    }

    public static byte[] CreateRptClose(int repeaterId)
    {
        var packet = new byte[RptCloseLength];
        "RPTCL"u8.CopyTo(packet);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(5), repeaterId);
        return packet;
    }

    public static bool TryReadRptAck(ReadOnlySpan<byte> packet, out uint value)
    {
        if (packet.Length >= RptAckLength && packet.StartsWith("RPTACK"u8))
        {
            value = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(6, 4));
            return true;
        }

        value = 0;
        return false;
    }

    public static bool TryReadMstNak(ReadOnlySpan<byte> packet, out int repeaterId)
    {
        if (packet.Length >= MstNakLength && packet.StartsWith("MSTNAK"u8))
        {
            repeaterId = BinaryPrimitives.ReadInt32BigEndian(packet.Slice(6, 4));
            return true;
        }

        repeaterId = 0;
        return false;
    }

    public static bool TryReadMstPong(ReadOnlySpan<byte> packet, out int repeaterId)
    {
        if (packet.Length >= MstPongLength && packet.StartsWith("MSTPONG"u8))
        {
            repeaterId = BinaryPrimitives.ReadInt32BigEndian(packet.Slice(7, 4));
            return true;
        }

        repeaterId = 0;
        return false;
    }

    public static bool IsDmrd(ReadOnlySpan<byte> packet) =>
        packet.Length >= MinimumDmrdLength && packet.StartsWith("DMRD"u8);

    public static int ReadDmrdSourceId(ReadOnlySpan<byte> packet)
    {
        EnsureDmrd(packet);
        return (packet[5] << 16) | (packet[6] << 8) | packet[7];
    }

    public static int ReadDmrdRepeaterId(ReadOnlySpan<byte> packet)
    {
        EnsureDmrd(packet);
        return BinaryPrimitives.ReadInt32BigEndian(packet.Slice(11, 4));
    }

    private static void EnsureDmrd(ReadOnlySpan<byte> packet)
    {
        if (!IsDmrd(packet)) throw new ArgumentException("A DMRD packet must contain at least 23 bytes.", nameof(packet));
    }

    private static void WriteFixedAscii(ref Span<byte> destination, string value, int length, string fieldName)
    {
        ArgumentNullException.ThrowIfNull(value, fieldName);
        EnsureAscii(value, fieldName);
        if (value.Length > length)
            throw new ArgumentException($"{fieldName} exceeds its {length}-byte RPTC field.", fieldName);

        Encoding.ASCII.GetBytes(value, destination[..length]);
        destination = destination[length..];
    }

    private static void EnsureAscii(string value, string parameterName)
    {
        foreach (var character in value)
        {
            if (character > 0x7F)
                throw new ArgumentException("Homebrew text fields and PSKs must contain ASCII characters only.", parameterName);
        }
    }
}
