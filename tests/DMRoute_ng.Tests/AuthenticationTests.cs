using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DMRoute_ng.Tests;

public class AuthenticationTests
{
    [Fact]
    public void Sha256Hash_WithKnownSaltAndPsk_ShouldMatchExpected()
    {
        // Arrange
        const uint randomNumber = 123456789;
        const string psk = "my_secret_key";

        var pskLength = Encoding.ASCII.GetByteCount(psk);
        Span<byte> dataToHash = stackalloc byte[4 + pskLength];
        
        BinaryPrimitives.WriteUInt32BigEndian(dataToHash[..4], randomNumber);
        Encoding.ASCII.GetBytes(psk, dataToHash[4..]);

        Span<byte> expectedHash = stackalloc byte[32];
        SHA256.HashData(dataToHash, expectedHash);

        // Act & Assert 
        var saltBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(saltBytes, randomNumber);
        var pskBytes = Encoding.ASCII.GetBytes(psk);
        
        byte[] combined = [.. saltBytes, .. pskBytes];
        var referenceHash = SHA256.HashData(combined);

        Assert.True(CryptographicOperations.FixedTimeEquals(expectedHash, referenceHash));
    }

    [Fact]
    public void MeshHmacSha256_WithStackalloc_ShouldMatchExpected()
    {
        // Arrange
        var meshPskBytes = "s3cr37m3sh"u8.ToArray();
        
        // Simulierter 32-Byte Nonce
        Span<byte> nonce = stackalloc byte[32];
        for (int i = 0; i < 32; i++) nonce[i] = (byte)i;

        Span<byte> expectedHash = stackalloc byte[32];
        HMACSHA256.HashData(meshPskBytes, nonce, expectedHash);

        // Act & Assert (Referenzberechnung)
        using var hmac = new HMACSHA256(meshPskBytes);
        var referenceHash = hmac.ComputeHash([.. nonce]);

        Assert.True(CryptographicOperations.FixedTimeEquals(expectedHash, referenceHash));
    }
}