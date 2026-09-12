using System.Buffers.Binary;
using System.Buffers.Text;
using System.Net;
using System.Net.Sockets;

namespace DMRoute_ng.Types;

public readonly struct Ipv4Endpoint(uint address, ushort port) : IEquatable<Ipv4Endpoint>
{
    public uint Address { get; } = address;
    public ushort Port { get; } = port;
    internal ulong PackedValue => ((ulong)Address << 16) | Port;

    internal static Ipv4Endpoint FromPackedValue(ulong value) => new((uint)(value >> 16), (ushort)value);

    public static Ipv4Endpoint FromSocketAddress(SocketAddress socketAddress)
    {
        if (socketAddress.Family != AddressFamily.InterNetwork || socketAddress.Size < 8)
        {
            throw new ArgumentException("Only IPv4 socket addresses are supported.", nameof(socketAddress));
        }

        var port = (ushort)((socketAddress[2] << 8) | socketAddress[3]);
        var address = ((uint)socketAddress[4] << 24)
                      | ((uint)socketAddress[5] << 16)
                      | ((uint)socketAddress[6] << 8)
                      | socketAddress[7];
        return new Ipv4Endpoint(address, port);
    }

    public static Ipv4Endpoint FromIPEndPoint(IPEndPoint endPoint)
    {
        Span<byte> bytes = stackalloc byte[4];
        if (endPoint.AddressFamily != AddressFamily.InterNetwork ||
            !endPoint.Address.TryWriteBytes(bytes, out var written) || written != bytes.Length)
        {
            throw new ArgumentException("Only IPv4 endpoints are supported.", nameof(endPoint));
        }

        return new Ipv4Endpoint(BinaryPrimitives.ReadUInt32BigEndian(bytes), checked((ushort)endPoint.Port));
    }

    public void WriteTo(SocketAddress socketAddress)
    {
        if (socketAddress.Family != AddressFamily.InterNetwork || socketAddress.Size < 16)
        {
            throw new ArgumentException("An IPv4 socket address with at least 16 bytes is required.", nameof(socketAddress));
        }

        socketAddress[2] = (byte)(Port >> 8);
        socketAddress[3] = (byte)Port;
        socketAddress[4] = (byte)(Address >> 24);
        socketAddress[5] = (byte)(Address >> 16);
        socketAddress[6] = (byte)(Address >> 8);
        socketAddress[7] = (byte)Address;
        for (var i = 8; i < socketAddress.Size; i++) socketAddress[i] = 0;
    }

    public IPAddress ToIPAddress()
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, Address);
        return new IPAddress(bytes);
    }

    public IPEndPoint ToIPEndPoint() => new(ToIPAddress(), Port);

    public bool TryFormatUtf8(Span<byte> destination, out int bytesWritten)
    {
        bytesWritten = 0;
        if (!TryAppendNumber(destination, ref bytesWritten, (byte)(Address >> 24)) || !TryAppendByte(destination, ref bytesWritten, (byte)'.') ||
            !TryAppendNumber(destination, ref bytesWritten, (byte)(Address >> 16)) || !TryAppendByte(destination, ref bytesWritten, (byte)'.') ||
            !TryAppendNumber(destination, ref bytesWritten, (byte)(Address >> 8)) || !TryAppendByte(destination, ref bytesWritten, (byte)'.') ||
            !TryAppendNumber(destination, ref bytesWritten, (byte)Address) || !TryAppendByte(destination, ref bytesWritten, (byte)':') ||
            !Utf8Formatter.TryFormat(Port, destination[bytesWritten..], out var portLength))
        {
            bytesWritten = 0;
            return false;
        }

        bytesWritten += portLength;
        return true;
    }

    private static bool TryAppendNumber(Span<byte> destination, ref int offset, byte value)
    {
        if (!Utf8Formatter.TryFormat(value, destination[offset..], out var length)) return false;
        offset += length;
        return true;
    }

    private static bool TryAppendByte(Span<byte> destination, ref int offset, byte value)
    {
        if (offset == destination.Length) return false;
        destination[offset++] = value;
        return true;
    }

    public bool Equals(Ipv4Endpoint other) => Address == other.Address && Port == other.Port;
    public override bool Equals(object? obj) => obj is Ipv4Endpoint other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Address, Port);
    public static bool operator ==(Ipv4Endpoint left, Ipv4Endpoint right) => left.Equals(right);
    public static bool operator !=(Ipv4Endpoint left, Ipv4Endpoint right) => !left.Equals(right);
    public override string ToString() => $"{ToIPAddress()}:{Port}";
}
