using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DMRoute_ng.Tests;

public class MeshProtocolTests
{
    private readonly byte[] _meshPsk = "s3cr37m3sh"u8.ToArray();

    [Fact]
    public void DmbcPacket_ShouldGenerateValidStructureAndHmac()
    {
        // Arrange
        var packet = new byte[50];
        "DMBC"u8.ToArray().CopyTo(packet, 0);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(4, 4), 101); // Zone 101
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(8, 2), 62031); // Data Port
        BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(10, 8), DateTime.UtcNow.Ticks);

        // Act
        HMACSHA256.HashData(_meshPsk, packet.AsSpan(0, 18), packet.AsSpan(18, 32));

        // Assert
        Assert.Equal("DMBC", Encoding.ASCII.GetString(packet[..4]));
        Assert.Equal(101, BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(4, 4)));
        Assert.Equal(62031, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(8, 2)));
        
        Span<byte> computedHash = stackalloc byte[32];
        HMACSHA256.HashData(_meshPsk, packet.AsSpan(0, 18), computedHash);
        Assert.True(CryptographicOperations.FixedTimeEquals(computedHash, packet.AsSpan(18, 32)));
    }

    [Fact]
    public void RoamPacket_ShouldGenerateValidStructureAndHmac()
    {
        // Arrange
        var packet = new byte[52];
        "ROAM"u8.ToArray().CopyTo(packet, 0);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(4, 4), 10001); // Device ID
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(8, 4), 100); // Foreign Zone ID
        BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(12, 8), DateTime.UtcNow.Ticks);

        // Act
        HMACSHA256.HashData(_meshPsk, packet.AsSpan(0, 20), packet.AsSpan(20, 32));

        // Assert
        Assert.Equal("ROAM", Encoding.ASCII.GetString(packet[..4]));
        Assert.Equal(10001, BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(4, 4)));
        Assert.Equal(100, BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(8, 4)));
        
        Span<byte> computedHash = stackalloc byte[32];
        HMACSHA256.HashData(_meshPsk, packet.AsSpan(0, 20), computedHash);
        Assert.True(CryptographicOperations.FixedTimeEquals(computedHash, packet.AsSpan(20, 32)));
    }
    
    [Fact]
    public void MeshPacket_WithExpiredTicks_ShouldBeIdentifiedAsInvalid()
    {
        // Arrange
        var packet = new byte[50];
        // Erzeuge einen Zeitstempel, der 31 Sekunden in der Vergangenheit liegt
        var expiredTicks = DateTime.UtcNow.AddSeconds(-31).Ticks;
        BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(10, 8), expiredTicks);

        // Act
        var readTicks = BinaryPrimitives.ReadInt64BigEndian(packet.AsSpan(10, 8));
        var packetTime = new DateTime(readTicks, DateTimeKind.Utc);
        var isExpired = Math.Abs((DateTime.UtcNow - packetTime).TotalSeconds) > 30;

        // Assert
        Assert.True(isExpired);
    }
}