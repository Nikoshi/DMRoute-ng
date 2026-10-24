using DMRoute_ng.Coding;
using DMRoute_ng.Core;
using DMRoute_ng.Registry;
using DMRoute_ng.Routing;
using DMRoute_ng.Services;
using DMRoute_ng.Types;
using Microsoft.Extensions.Logging.Abstractions;

namespace DMRoute_ng.Tests;

[Collection(nameof(RadioCheckReachabilityCollection))]
public sealed class RadioCheckReachabilityTests
{
    [Fact]
    public void MeshControlProtocol_RoundTripsAndRejectsTamperingAndOldPackets()
    {
        var key = "mesh-secret"u8;
        var now = DateTime.UtcNow.Ticks;
        var expected = new MeshControlMessage(
            MeshControlKind.RouteQuery,
            0x6400000001,
            100,
            10001,
            10101,
            Scope: MeshRouteQueryScope.Whois);
        Span<byte> packet = stackalloc byte[MeshControlProtocol.PacketLength];

        MeshControlProtocol.Write(packet, in expected, key, now);

        Assert.True(MeshControlProtocol.TryRead(packet, key, now, out var actual));
        Assert.Equal(expected, actual);

        packet[24] ^= 0x01;
        Assert.False(MeshControlProtocol.TryRead(packet, key, now, out _));
        packet[24] ^= 0x01;
        Assert.False(MeshControlProtocol.TryRead(packet, key, now + TimeSpan.FromSeconds(31).Ticks, out _));
    }

    [Fact]
    public void Outbox_PersistsDeduplicatesAndExpiresEntries()
    {
        var directory = ConfigurationTests.CreateTemporaryDirectory();
        var path = Path.Combine(directory, "outbox.json");
        var now = DateTime.UtcNow.Ticks;
        try
        {
            var first = new RadioCheckOutbox(
                NullLogger<RadioCheckOutbox>.Instance, path, capacity: 2, now);
            var entry = new RadioCheckOutboxEntry
            {
                MessageId = 1,
                OriginZone = 100,
                HomeZone = 101,
                SourceId = 10001,
                TargetId = 10101,
                CreatedAtTicks = now,
                ExpiresAtTicks = now + TimeSpan.FromHours(24).Ticks
            };
            Assert.True(first.TryAdd(in entry));

            var duplicate = entry with { MessageId = 2, CreatedAtTicks = now + TimeSpan.FromMinutes(1).Ticks };
            Assert.True(first.TryAdd(in duplicate));
            Assert.Equal(1, first.Count);

            var reloaded = new RadioCheckOutbox(
                NullLogger<RadioCheckOutbox>.Instance, path, capacity: 2, now);
            var snapshot = reloaded.Snapshot(now);
            Assert.Single(snapshot);
            Assert.Equal(1UL, snapshot[0].MessageId);

            Assert.Empty(reloaded.Snapshot(now + TimeSpan.FromHours(25).Ticks));
            var afterExpiry = new RadioCheckOutbox(
                NullLogger<RadioCheckOutbox>.Instance, path, capacity: 2, now + TimeSpan.FromHours(25).Ticks);
            Assert.Equal(0, afterExpiry.Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void UnknownRadioCheck_RaisesOneBoundedMissWithoutAllocatingAfterWarmup()
    {
        using var fixture = new RouterFixture();
        Span<byte> burst = stackalloc byte[DmrBurstWriter.BurstLength];
        Span<byte> packet = stackalloc byte[DmrBurstWriter.DmrdLength];
        DmrBurstWriter.WriteRadioCheckRequest(burst, 10001, 10101, 1);
        DmrBurstWriter.WriteDmrd(packet, burst, 1, 10001, 10101, 1000001, 0x03, 1);
        var misses = 0;
        var lastSource = 0;
        var lastTarget = 0;
        var lastHotspot = 0;
        fixture.Router.OnRadioCheckRouteMiss += HandleMiss;

        for (var index = 0; index < 32; index++)
            fixture.Router.RouteDmrd(packet, fixture.SourceEndpoint, fixture.Sender);
        misses = 0;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1000; index++)
            fixture.Router.RouteDmrd(packet, fixture.SourceEndpoint, fixture.Sender);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(1000, misses);
        Assert.Equal(10001, lastSource);
        Assert.Equal(10101, lastTarget);
        Assert.Equal(1000001, lastHotspot);
        return;

        void HandleMiss(in RadioCheckRouteMiss miss)
        {
            lastSource = miss.SourceId;
            lastTarget = miss.TargetId;
            lastHotspot = miss.SourceHotspotId;
            misses++;
        }
    }

    [Fact]
    public void RadioCheckAcknowledgement_RaisesEventWithoutAllocatingAfterWarmup()
    {
        using var fixture = new RouterFixture();
        Span<byte> burst = stackalloc byte[DmrBurstWriter.BurstLength];
        Span<byte> packet = stackalloc byte[DmrBurstWriter.DmrdLength];
        DmrBurstWriter.WriteRadioCheckAcknowledgement(burst, 10002, 10001, 1);
        DmrBurstWriter.WriteDmrd(packet, burst, 1, 10002, 10001, 1000001, 0x03, 1);
        var acknowledgements = 0;
        var lastSource = 0;
        var lastDestination = 0;
        fixture.Router.OnRadioCheckAcknowledgement += HandleAcknowledgement;

        for (var index = 0; index < 256; index++)
            fixture.Router.RouteDmrd(packet, fixture.SourceEndpoint, fixture.Sender);
        acknowledgements = 0;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1000; index++)
            fixture.Router.RouteDmrd(packet, fixture.SourceEndpoint, fixture.Sender);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(1000, acknowledgements);
        Assert.Equal(10002, lastSource);
        Assert.Equal(10001, lastDestination);
        return;

        void HandleAcknowledgement(in RadioCheckAcknowledgement acknowledgement)
        {
            lastSource = acknowledgement.SourceId;
            lastDestination = acknowledgement.DestinationId;
            acknowledgements++;
        }
    }

    [Fact]
    public void KnownHomeMaster_KeepsRadioCheckOnNormalFastPath()
    {
        using var fixture = new RouterFixture();
        fixture.Masters.AddOrUpdate(101, new Ipv4Endpoint(0x7F000001, 62101));
        Span<byte> burst = stackalloc byte[DmrBurstWriter.BurstLength];
        Span<byte> packet = stackalloc byte[DmrBurstWriter.DmrdLength];
        DmrBurstWriter.WriteRadioCheckRequest(burst, 10001, 10101, 1);
        DmrBurstWriter.WriteDmrd(packet, burst, 1, 10001, 10101, 1000001, 0x03, 1);
        var misses = 0;
        fixture.Router.OnRadioCheckRouteMiss += CountMiss;

        fixture.Router.RouteDmrd(packet, fixture.SourceEndpoint, fixture.Sender);

        Assert.Equal(0, misses);
        Assert.Equal(1, fixture.Sender.SendCount);
        return;

        void CountMiss(in RadioCheckRouteMiss _) => misses++;
    }

    private sealed class CountingSender : IDmrSender
    {
        public int SendCount { get; private set; }
        public void SendTo(ReadOnlySpan<byte> packet, Ipv4Endpoint endPoint) => SendCount++;
    }

    private sealed class RouterFixture : IDisposable
    {
        public RepeaterRegistry Repeaters { get; }
        public MasterRegistry Masters { get; }
        public RoamingRegistry Roaming { get; }
        public MeshDiscoveryService Mesh { get; }
        public MicroSubnetRouter Router { get; }
        public CountingSender Sender { get; } = new();
        public Ipv4Endpoint SourceEndpoint { get; } = new(0x7F000001, 62031);

        public RouterFixture()
        {
            Repeaters = new RepeaterRegistry(NullLogger<RepeaterRegistry>.Instance, 100, "secret");
            Masters = new MasterRegistry(NullLogger<MasterRegistry>.Instance);
            Roaming = new RoamingRegistry(NullLogger<RoamingRegistry>.Instance, 8);
            Mesh = new MeshDiscoveryService(
                NullLogger<MeshDiscoveryService>.Instance, Masters, Roaming, 100, 62031, 0, "mesh");
            Router = new MicroSubnetRouter(
                NullLogger<MicroSubnetRouter>.Instance, Repeaters, Masters, Roaming, Mesh, 100, 8, 8);
            Repeaters.TryGet(1000001, out var source);
            source.EndPoint = SourceEndpoint;
            source.State = RepeaterState.LoggedIn;
            Repeaters.RefreshRoutingSnapshot();
        }

        public void Dispose()
        {
            Router.Dispose();
            Mesh.Dispose();
            Roaming.Dispose();
            Masters.Dispose();
            Repeaters.Dispose();
        }
    }
}

[CollectionDefinition(nameof(RadioCheckReachabilityCollection), DisableParallelization = true)]
public sealed class RadioCheckReachabilityCollection;
