using System.Buffers.Binary;
using System.Security.Cryptography;

namespace DMRoute_ng.Core;

public enum MeshControlKind : byte
{
    RouteQuery = 1,
    RouteResult = 2,
    RadioCheck = 3,
    FallbackTransfer = 4,
    FallbackAccepted = 5,
    FallbackDeliver = 6,
    FallbackResult = 7
}

public enum MeshRouteQueryScope : byte
{
    Home = 0,
    Confirm = 1,
    Whois = 2
}

public enum MeshRouteStatus : byte
{
    NotFound = 0,
    Found = 1
}

public readonly record struct MeshControlMessage(
    MeshControlKind Kind,
    ulong CorrelationId,
    int OriginZone,
    int SourceId,
    int TargetId,
    int RouteZone = 0,
    long ObservedAtTicks = 0,
    long CreatedAtTicks = 0,
    long ExpiresAtTicks = 0,
    MeshRouteQueryScope Scope = MeshRouteQueryScope.Home,
    MeshRouteStatus Status = MeshRouteStatus.NotFound);

/// <summary>
/// Kodiert und prüft authentisierte Kontrollnachrichten zwischen DMRoute-ng-Mastern.
/// </summary>
/// <remarks>
/// Das Wire-Format ist exakt <see cref="PacketLength"/> Byte lang. Die ersten 64 Byte
/// enthalten Version, Typ, Adressen und Zeitwerte; die letzten 32 Byte enthalten den
/// HMAC-SHA-256 über den Nutzteil. Empfangene Pakete außerhalb des erlaubten
/// Zeitfensters werden auch bei gültigem HMAC verworfen.
/// </remarks>
public static class MeshControlProtocol
{
    /// <summary>Feste Länge einer Kontrollnachricht einschließlich HMAC.</summary>
    public const int PacketLength = 96;
    private const byte Version = 1;
    private static readonly TimeSpan MaximumClockSkew = TimeSpan.FromSeconds(30);

    /// <summary>Schreibt eine vollständige Kontrollnachricht in den Zielpuffer.</summary>
    /// <exception cref="ArgumentException">
    /// Der Zielpuffer ist kleiner als <see cref="PacketLength"/>.
    /// </exception>
    public static void Write(
        Span<byte> destination,
        in MeshControlMessage message,
        ReadOnlySpan<byte> meshKey,
        long sentAtTicks)
    {
        if (destination.Length < PacketLength)
            throw new ArgumentException($"Mesh control output must contain {PacketLength} bytes.", nameof(destination));

        destination = destination[..PacketLength];
        destination.Clear();
        "DMCX"u8.CopyTo(destination);
        destination[4] = Version;
        destination[5] = (byte)message.Kind;
        destination[6] = (byte)message.Scope;
        destination[7] = (byte)message.Status;
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..16], message.CorrelationId);
        BinaryPrimitives.WriteInt32BigEndian(destination[16..20], message.OriginZone);
        BinaryPrimitives.WriteInt32BigEndian(destination[20..24], message.SourceId);
        BinaryPrimitives.WriteInt32BigEndian(destination[24..28], message.TargetId);
        BinaryPrimitives.WriteInt32BigEndian(destination[28..32], message.RouteZone);
        BinaryPrimitives.WriteInt64BigEndian(destination[32..40], message.ObservedAtTicks);
        BinaryPrimitives.WriteInt64BigEndian(destination[40..48], message.CreatedAtTicks);
        BinaryPrimitives.WriteInt64BigEndian(destination[48..56], message.ExpiresAtTicks);
        BinaryPrimitives.WriteInt64BigEndian(destination[56..64], sentAtTicks);
        HMACSHA256.HashData(meshKey, destination[..64], destination[64..96]);
    }

    /// <summary>
    /// Prüft Länge, Kennung, Version, HMAC, Zeitfenster und Wertebereiche und dekodiert
    /// anschließend die Nachricht.
    /// </summary>
    public static bool TryRead(
        ReadOnlySpan<byte> packet,
        ReadOnlySpan<byte> meshKey,
        long nowTicks,
        out MeshControlMessage message)
    {
        message = default;
        if (packet.Length != PacketLength || !packet[..4].SequenceEqual("DMCX"u8) || packet[4] != Version)
            return false;

        Span<byte> hash = stackalloc byte[32];
        HMACSHA256.HashData(meshKey, packet[..64], hash);
        if (!CryptographicOperations.FixedTimeEquals(hash, packet[64..96])) return false;

        var sentAtTicks = BinaryPrimitives.ReadInt64BigEndian(packet[56..64]);
        if (sentAtTicks <= 0 || Math.Abs(nowTicks - sentAtTicks) > MaximumClockSkew.Ticks) return false;

        var kind = (MeshControlKind)packet[5];
        if (kind is < MeshControlKind.RouteQuery or > MeshControlKind.FallbackResult) return false;
        var scope = (MeshRouteQueryScope)packet[6];
        if (scope is < MeshRouteQueryScope.Home or > MeshRouteQueryScope.Whois) return false;
        var status = (MeshRouteStatus)packet[7];
        if (status is < MeshRouteStatus.NotFound or > MeshRouteStatus.Found) return false;

        message = new MeshControlMessage(
            kind,
            BinaryPrimitives.ReadUInt64BigEndian(packet[8..16]),
            BinaryPrimitives.ReadInt32BigEndian(packet[16..20]),
            BinaryPrimitives.ReadInt32BigEndian(packet[20..24]),
            BinaryPrimitives.ReadInt32BigEndian(packet[24..28]),
            BinaryPrimitives.ReadInt32BigEndian(packet[28..32]),
            BinaryPrimitives.ReadInt64BigEndian(packet[32..40]),
            BinaryPrimitives.ReadInt64BigEndian(packet[40..48]),
            BinaryPrimitives.ReadInt64BigEndian(packet[48..56]),
            scope,
            status);
        return true;
    }
}
