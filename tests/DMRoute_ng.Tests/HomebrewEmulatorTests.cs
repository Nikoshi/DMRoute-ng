using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using DMRoute_ng.Core;
using DMRoute_ng.Emulator;
using DMRoute_ng.Registry;
using DMRoute_ng.Routing;
using DMRoute_ng.Types;
using Microsoft.Extensions.Logging.Abstractions;

namespace DMRoute_ng.Tests;

public sealed class HomebrewEmulatorTests
{
    private static readonly byte[] CapturedGroupDataFrame = Convert.FromHexString(
        "444D5244050027110F1B93000F4241A6A3E1BE9413ED97E6073B088BA733CF8F058D5D7F77FD757338C1C05A79680661090210199A");

    [Fact]
    public void HomebrewPacketWriters_UseExpectedWireLayout()
    {
        const int repeaterId = 1000001;
        const uint salt = 0x1EC403A3;
        const string psk = "s3cr37w0rd1000001";

        Assert.Equal("5250544C000F4241", Convert.ToHexString(HomebrewProtocol.CreateRptl(repeaterId)));
        Assert.Equal("52505450494E47000F4241", Convert.ToHexString(HomebrewProtocol.CreateRptPing(repeaterId)));
        Assert.Equal("525054434C000F4241", Convert.ToHexString(HomebrewProtocol.CreateRptClose(repeaterId)));

        var rptk = HomebrewProtocol.CreateRptk(repeaterId, salt, psk);
        Assert.Equal(HomebrewProtocol.RptkLength, rptk.Length);
        Assert.True(rptk.AsSpan(0, 4).SequenceEqual("RPTK"u8));
        Assert.Equal(repeaterId, BinaryPrimitives.ReadInt32BigEndian(rptk.AsSpan(4, 4)));

        Span<byte> hashInput = stackalloc byte[4 + psk.Length];
        BinaryPrimitives.WriteUInt32BigEndian(hashInput, salt);
        Encoding.ASCII.GetBytes(psk, hashInput[4..]);
        Span<byte> expectedHash = stackalloc byte[32];
        SHA256.HashData(hashInput, expectedHash);
        Assert.True(rptk.AsSpan(8).SequenceEqual(expectedHash));

        var config = new HotspotConfiguration
        {
            Callsign = "M1ABC",
            RxFrequency = "446006250",
            TxFrequency = "446006250",
            TxPower = 1,
            ColorCode = 1,
            Latitude = "50.00000",
            Longitude = "-3.000000",
            Height = 4,
            Location = "Town",
            Description = "Country",
            SoftwareId = "20240210_PS4",
            PackageId = "MMDVM_HS_Dual_Hat"
        };
        var rptc = HomebrewProtocol.CreateRptc(repeaterId, config);
        Assert.Equal(HomebrewProtocol.RptcLength, rptc.Length);
        Assert.True(rptc.AsSpan(0, 4).SequenceEqual("RPTC"u8));
        Assert.Equal(repeaterId, BinaryPrimitives.ReadInt32BigEndian(rptc.AsSpan(4, 4)));
        Assert.Equal("M1ABC   ", Encoding.ASCII.GetString(rptc, 8, 8));
        Assert.Equal("446006250", Encoding.ASCII.GetString(rptc, 16, 9));
        Assert.Equal("0101", Encoding.ASCII.GetString(rptc, 34, 4));
        Assert.Equal("50.00000", Encoding.ASCII.GetString(rptc, 38, 8));
        Assert.Equal("-3.000000", Encoding.ASCII.GetString(rptc, 46, 9));
        Assert.Equal("004", Encoding.ASCII.GetString(rptc, 55, 3));
    }

    [Fact]
    public void HomebrewResponseParsers_RejectTruncatedPackets()
    {
        Assert.False(HomebrewProtocol.TryReadRptAck("RPTACK"u8, out _));
        Assert.False(HomebrewProtocol.TryReadMstNak("MSTNAK"u8, out _));
        Assert.False(HomebrewProtocol.TryReadMstPong("MSTPONG"u8, out _));

        Span<byte> ack = stackalloc byte[HomebrewProtocol.RptAckLength];
        "RPTACK"u8.CopyTo(ack);
        BinaryPrimitives.WriteUInt32BigEndian(ack[6..], 0x12345678);
        Assert.True(HomebrewProtocol.TryReadRptAck(ack, out var value));
        Assert.Equal(0x12345678u, value);
    }

    [Fact]
    public async Task TwoHotspots_LoginPingAndRouteCapturedScenarioOverLoopback()
    {
        await using var fixture = await DmrLoopbackFixture.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var source = new VirtualHotspot(CreateOptions(fixture.EndPoint, 1000001));
        await using var target = new VirtualHotspot(CreateOptions(fixture.EndPoint, 1000002));

        await source.LoginAsync(timeout.Token);
        await target.LoginAsync(timeout.Token);
        await source.PingAsync(timeout.Token);
        await target.PingAsync(timeout.Token);

        Assert.Equal(VirtualHotspotState.Configured, source.State);
        Assert.True(fixture.Repeaters.TryGetExisting(1000001, out var sourceRepeater));
        Assert.Equal(RepeaterState.LoggedIn, sourceRepeater.State);
        Assert.Equal("DMRTEST", sourceRepeater.Configuration?.Callsign);

        var scenario = new RadioScenario(
            "captured fixed-position group data",
            10001,
            [new RadioScenarioStep(CapturedGroupDataFrame, TimeSpan.Zero)]);
        var radio = new VirtualRadio(10001);
        await radio.ReplayAsync(source, scenario, timeout.Token);

        var forwarded = await target.ReceiveDmrdAsync(timeout.Token);
        Assert.Equal(CapturedGroupDataFrame, forwarded.Payload);
        await Assert.ThrowsAsync<HomebrewTimeoutException>(
            () => source.ReceiveDmrdAsync(TimeSpan.FromMilliseconds(150), timeout.Token));
    }

    [Fact]
    public async Task Login_WithWrongPassword_IsRejected()
    {
        await using var fixture = await DmrLoopbackFixture.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var hotspot = new VirtualHotspot(CreateOptions(fixture.EndPoint, 1000001) with
        {
            PreSharedKey = "wrong-password"
        });

        var exception = await Assert.ThrowsAsync<HomebrewRejectedException>(
            () => hotspot.LoginAsync(timeout.Token));

        Assert.Equal(1000001, exception.RepeaterId);
        Assert.Equal(VirtualHotspotState.Rejected, hotspot.State);
        Assert.True(fixture.Repeaters.TryGetExisting(1000001, out var repeater));
        Assert.NotEqual(RepeaterState.LoggedIn, repeater.State);
    }

    [Fact]
    public async Task Login_WithForeignZoneId_IsRejectedWithoutEnrollment()
    {
        await using var fixture = await DmrLoopbackFixture.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var hotspot = new VirtualHotspot(CreateOptions(fixture.EndPoint, 1010001));

        var exception = await Assert.ThrowsAsync<HomebrewRejectedException>(
            () => hotspot.LoginAsync(timeout.Token));

        Assert.Equal(1010001, exception.RepeaterId);
        Assert.False(fixture.Repeaters.TryGetExisting(1010001, out _));
    }

    [Fact]
    public async Task TimedOutHotspot_SoftReconnectsAndCanPerformFullLoginAgain()
    {
        await using var fixture = await DmrLoopbackFixture.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var hotspot = new VirtualHotspot(CreateOptions(fixture.EndPoint, 1000001));
        await hotspot.LoginAsync(timeout.Token);

        Assert.True(fixture.Repeaters.TryGetExisting(1000001, out var repeater));
        var lastPing = Volatile.Read(ref repeater.LastPingTicks);
        fixture.Repeaters.DisconnectIdleRepeaters(lastPing + TimeSpan.FromSeconds(46).Ticks);
        Assert.Equal(RepeaterState.Disconnected, repeater.State);

        await hotspot.PingAsync(timeout.Token);
        Assert.Equal(RepeaterState.LoggedIn, repeater.State);

        await hotspot.DisconnectAsync(timeout.Token);
        await WaitUntilAsync(() => repeater.State == RepeaterState.Disconnected, timeout.Token);
        await hotspot.LoginAsync(timeout.Token);
        Assert.Equal(VirtualHotspotState.Configured, hotspot.State);
        Assert.Equal(RepeaterState.LoggedIn, repeater.State);
    }

    [Fact]
    public async Task ConfiguredKeepalive_SendsPeriodicPing()
    {
        await using var fixture = await DmrLoopbackFixture.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var hotspot = new VirtualHotspot(CreateOptions(fixture.EndPoint, 1000001) with
        {
            KeepaliveInterval = TimeSpan.FromMilliseconds(20)
        });
        await hotspot.LoginAsync(timeout.Token);

        Assert.True(fixture.Repeaters.TryGetExisting(1000001, out var repeater));
        var loginTicks = Volatile.Read(ref repeater.LastPingTicks);
        await WaitUntilAsync(() => Volatile.Read(ref repeater.LastPingTicks) > loginTicks, timeout.Token);

        Assert.Equal(VirtualHotspotState.Configured, hotspot.State);
    }

    [Fact]
    public void RadioScenario_RejectsFramesFromAnotherRadio()
    {
        Assert.Throws<ArgumentException>(() => new RadioScenario(
            "wrong source",
            10002,
            [new RadioScenarioStep(CapturedGroupDataFrame, TimeSpan.Zero)]));
    }

    private static VirtualHotspotOptions CreateOptions(IPEndPoint masterEndPoint, int repeaterId) => new()
    {
        MasterEndPoint = masterEndPoint,
        RepeaterId = repeaterId,
        PreSharedKey = $"secret{repeaterId}",
        OperationTimeout = TimeSpan.FromSeconds(1),
        ReceiveQueueCapacity = 16
    };

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition()) await Task.Delay(10, cancellationToken);
    }

    private sealed class DmrLoopbackFixture : IAsyncDisposable
    {
        private readonly MasterRegistry _masters;
        private readonly RoamingRegistry _roaming;
        private readonly MeshDiscoveryService _mesh;
        private readonly MicroSubnetRouter _router;
        private readonly DmrServer _server;

        private DmrLoopbackFixture()
        {
            Repeaters = new RepeaterRegistry(NullLogger<RepeaterRegistry>.Instance, 100, "secret");
            _masters = new MasterRegistry(NullLogger<MasterRegistry>.Instance);
            _roaming = new RoamingRegistry(NullLogger<RoamingRegistry>.Instance, 16);
            _mesh = new MeshDiscoveryService(
                NullLogger<MeshDiscoveryService>.Instance, _masters, _roaming, 100, 62031, 0, "mesh");
            _router = new MicroSubnetRouter(
                NullLogger<MicroSubnetRouter>.Instance, Repeaters, _masters, _roaming, _mesh, 100, 16, 16);
            _server = new DmrServer(
                NullLogger<DmrServer>.Instance,
                Repeaters,
                _router,
                new IPEndPoint(IPAddress.Loopback, 0));
        }

        public RepeaterRegistry Repeaters { get; }
        public IPEndPoint EndPoint { get; private set; } = null!;

        public static async Task<DmrLoopbackFixture> StartAsync()
        {
            var fixture = new DmrLoopbackFixture();
            await fixture._server.StartAsync(CancellationToken.None);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            fixture.EndPoint = await fixture._server.WaitUntilBoundAsync(timeout.Token);
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _server.StopAsync(timeout.Token);
            _server.Dispose();
            _router.Dispose();
            _mesh.Dispose();
            _roaming.Dispose();
            _masters.Dispose();
            Repeaters.Dispose();
        }
    }
}
