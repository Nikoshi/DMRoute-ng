using System.Buffers;
using System.Buffers.Binary;
using System.Net.Sockets;

namespace DMRoute_ng.Integration;

public sealed class RawMqttClient(byte[] clientId) : IDisposable
{
    private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

    public async Task<bool> ConnectAsync(string host, int port)
    {
        await _socket.ConnectAsync(host, port);

        var buffer = ArrayPool<byte>.Shared.Rent(128);
        try
        {
            Span<byte> span = buffer;
            ReadOnlySpan<byte> protoName = "MQTT"u8;

            span[0] = 0x10; // CONNECT

            var varHeaderLen = 2 + protoName.Length + 1 + 1 + 2;
            var payloadLen = 2 + clientId.Length;
            var remainingLength = varHeaderLen + payloadLen;

            var lenBytesCount = WriteVariableLength(span.Slice(1), remainingLength);
            var currentIdx = 1 + lenBytesCount;

            BinaryPrimitives.WriteUInt16BigEndian(span.Slice(currentIdx, 2), (ushort)protoName.Length);
            currentIdx += 2;

            protoName.CopyTo(span.Slice(currentIdx, protoName.Length));
            currentIdx += protoName.Length;

            span[currentIdx++] = 0x04; // Level (3.1.1)
            span[currentIdx++] = 0x02; // Flags (Clean Session)
            
            BinaryPrimitives.WriteUInt16BigEndian(span.Slice(currentIdx, 2), 60); // KeepAlive
            currentIdx += 2;

            BinaryPrimitives.WriteUInt16BigEndian(span.Slice(currentIdx, 2), (ushort)clientId.Length);
            currentIdx += 2;

            clientId.CopyTo(span.Slice(currentIdx, clientId.Length));
            currentIdx += clientId.Length;

            await _socket.SendAsync(buffer.AsMemory(0, currentIdx), SocketFlags.None);

            var received = await _socket.ReceiveAsync(buffer.AsMemory(0, 4), SocketFlags.None, CancellationToken.None);
            
            return received >= 4 && buffer[0] == 0x20 && buffer[1] == 0x02 && buffer[3] == 0x00;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Publish(ReadOnlySpan<byte> topic, ReadOnlySpan<byte> payload, bool retain = false)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(topic.Length + payload.Length + 16);
        try
        {
            Span<byte> span = buffer;
            
            var innerLength = 2 + topic.Length + payload.Length;
            
            Span<byte> tempLenBuf = stackalloc byte[4];
            var lenBytesCount = WriteVariableLength(tempLenBuf, innerLength);

            var currentIdx = 0;
            span[currentIdx++] = (byte)(retain ? 0x31 : 0x30); // PUBLISH (QoS 0)
            
            tempLenBuf.Slice(0, lenBytesCount).CopyTo(span.Slice(currentIdx, lenBytesCount));
            currentIdx += lenBytesCount;

            BinaryPrimitives.WriteUInt16BigEndian(span.Slice(currentIdx, 2), (ushort)topic.Length);
            currentIdx += 2;

            topic.CopyTo(span.Slice(currentIdx, topic.Length));
            currentIdx += topic.Length;

            payload.CopyTo(span.Slice(currentIdx, payload.Length));
            currentIdx += payload.Length;

            _socket.Send(span.Slice(0, currentIdx));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
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
        _socket.Dispose();
    }
}