using System.Buffers.Binary;
using System.Net.Sockets;

namespace DMRoute_ng.Integration;

public sealed class RawMqttClient : IDisposable
{
    private const int DefaultMaxPacketSize = 64 * 1024;
    private readonly byte[] _clientId;
    private readonly byte[] _buffer;
    private readonly object _sync = new();
    private Socket? _socket;

    public RawMqttClient(byte[] clientId, int maxPacketSize = DefaultMaxPacketSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPacketSize, 128);
        _clientId = clientId;
        _buffer = GC.AllocateUninitializedArray<byte>(maxPacketSize, pinned: true);
    }

    public async Task<bool> ConnectAsync(string host, int port)
    {
        Socket socket;
        lock (_sync)
        {
            _socket?.Dispose();
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _socket = socket;
        }

        try
        {
            await socket.ConnectAsync(host, port).ConfigureAwait(false);
            var packetLength = WriteConnectPacket(_buffer);
            await SendAllAsync(socket, _buffer.AsMemory(0, packetLength)).ConfigureAwait(false);
            await ReceiveExactAsync(socket, _buffer.AsMemory(0, 4)).ConfigureAwait(false);
            return _buffer[0] == 0x20 && _buffer[1] == 0x02 && _buffer[2] == 0x00 && _buffer[3] == 0x00;
        }
        catch
        {
            lock (_sync)
            {
                if (ReferenceEquals(_socket, socket)) _socket = null;
            }
            socket.Dispose();
            throw;
        }
    }

    public void Publish(ReadOnlySpan<byte> topic, ReadOnlySpan<byte> payload, bool retain = false)
    {
        if (topic.IsEmpty || topic.Length > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(topic));
        var innerLength = checked(2 + topic.Length + payload.Length);
        var required = checked(1 + VariableLengthByteCount(innerLength) + innerLength);
        if (required > _buffer.Length) throw new ArgumentException("MQTT packet exceeds the configured buffer size.");

        lock (_sync)
        {
            var socket = _socket ?? throw new InvalidOperationException("MQTT client is not connected.");
            var span = _buffer.AsSpan(0, required);
            var currentIndex = 0;
            span[currentIndex++] = (byte)(retain ? 0x31 : 0x30);
            currentIndex += WriteVariableLength(span[currentIndex..], innerLength);
            BinaryPrimitives.WriteUInt16BigEndian(span.Slice(currentIndex, 2), (ushort)topic.Length);
            currentIndex += 2;
            topic.CopyTo(span[currentIndex..]);
            currentIndex += topic.Length;
            payload.CopyTo(span[currentIndex..]);
            currentIndex += payload.Length;

            var sent = 0;
            while (sent < currentIndex)
            {
                var count = socket.Send(span[sent..currentIndex], SocketFlags.None);
                if (count == 0) throw new SocketException((int)SocketError.ConnectionReset);
                sent += count;
            }
        }
    }

    private int WriteConnectPacket(Span<byte> span)
    {
        ReadOnlySpan<byte> protocolName = "MQTT"u8;
        var remainingLength = 2 + protocolName.Length + 1 + 1 + 2 + 2 + _clientId.Length;
        var required = 1 + VariableLengthByteCount(remainingLength) + remainingLength;
        if (_clientId.Length > ushort.MaxValue || required > span.Length)
            throw new ArgumentException("MQTT client ID exceeds the configured buffer size.");

        var currentIndex = 0;
        span[currentIndex++] = 0x10;
        currentIndex += WriteVariableLength(span[currentIndex..], remainingLength);
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(currentIndex, 2), (ushort)protocolName.Length);
        currentIndex += 2;
        protocolName.CopyTo(span[currentIndex..]);
        currentIndex += protocolName.Length;
        span[currentIndex++] = 0x04;
        span[currentIndex++] = 0x02;
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(currentIndex, 2), 60);
        currentIndex += 2;
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(currentIndex, 2), (ushort)_clientId.Length);
        currentIndex += 2;
        _clientId.CopyTo(span[currentIndex..]);
        return currentIndex + _clientId.Length;
    }

    private static async Task SendAllAsync(Socket socket, ReadOnlyMemory<byte> packet)
    {
        var sent = 0;
        while (sent < packet.Length)
        {
            var count = await socket.SendAsync(packet[sent..], SocketFlags.None).ConfigureAwait(false);
            if (count == 0) throw new SocketException((int)SocketError.ConnectionReset);
            sent += count;
        }
    }

    private static async Task ReceiveExactAsync(Socket socket, Memory<byte> target)
    {
        var received = 0;
        while (received < target.Length)
        {
            var count = await socket.ReceiveAsync(target[received..], SocketFlags.None).ConfigureAwait(false);
            if (count == 0) throw new SocketException((int)SocketError.ConnectionReset);
            received += count;
        }
    }

    private static int VariableLengthByteCount(int length)
    {
        var count = 1;
        while ((length /= 128) > 0) count++;
        return count;
    }

    private static int WriteVariableLength(Span<byte> target, int length)
    {
        var bytesWritten = 0;
        do
        {
            var encodedByte = (byte)(length % 128);
            length /= 128;
            if (length > 0) encodedByte |= 0x80;
            target[bytesWritten++] = encodedByte;
        } while (length > 0);
        return bytesWritten;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _socket?.Dispose();
            _socket = null;
        }
    }
}
