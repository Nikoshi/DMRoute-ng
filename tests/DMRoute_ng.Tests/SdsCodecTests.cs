using System.Buffers.Binary;
using System.Text;
using DMRoute_ng.Coding;
using DMRoute_ng.Configuration;
using DMRoute_ng.Core;
using DMRoute_ng.Gateways;
using DMRoute_ng.Registry;
using DMRoute_ng.Routing;
using DMRoute_ng.Services;
using DMRoute_ng.Types;
using Microsoft.Extensions.Logging.Abstractions;

namespace DMRoute_ng.Tests;

public sealed class SdsCodecTests
{
    private const string CapturedHeader =
        "03223697255A495224314323058D5D7F77FD75733810044471D40C830A4409B5D1";
    private const string CapturedFirstBlock =
        "2E32A22FECF9E282222228B2063D5D7F77FD757A872E24222E3D23B8222275EBE5";

    [Fact]
    public void TmsWriter_ReproducesCapturedUtf16LittleEndianDatagram()
    {
        Span<byte> packet = stackalloc byte[128];
        Assert.True(TmsMessageWriter.TryWriteUtf16LittleEndian(
            packet, 10001, 10101, Encoding.Unicode.GetBytes("DMR TEST 1"), out var written));

        Assert.Equal(58, written);
        Assert.Equal(
            "4500003A000000004011142E0C0027110C0027750FA70FA700266DAA001CE00081040D000A0044004D0052002000540045005300540020003100",
            Convert.ToHexString(packet[..written]));
    }

    [Fact]
    public void BptcAndTrellisWriters_ReproduceCapturedBursts()
    {
        Span<byte> header = stackalloc byte[33];
        DmrBurstWriter.WriteConfirmedDataHeader(header, 10001, 10101, 4, 2, 0, 1);
        Assert.Equal(CapturedHeader, Convert.ToHexString(header));

        Span<byte> block = stackalloc byte[33];
        DmrBurstWriter.WriteConfirmedRateThreeQuarterBlock(
            block, Convert.FromHexString("4500003A000000004011142E0C002711"), 0, 1);
        Assert.Equal(CapturedFirstBlock, Convert.ToHexString(block));

        Span<byte> decoded = stackalloc byte[18];
        Assert.True(DmrTrellis.Decode(block, decoded));
        Assert.Equal("006C4500003A000000004011142E0C002711", Convert.ToHexString(decoded));

        Span<byte> response = stackalloc byte[33];
        DmrBurstWriter.WriteConfirmedResponseHeader(response, 10101, 10001, 0, true, 1);
        Assert.Equal(
            "037134AD07800A5037C14B62458D5D7F77FD757338E8010872762E03431C013144",
            Convert.ToHexString(response));
    }

    [Fact]
    public void CrcWriters_MatchCapturedConfirmedMessage()
    {
        var messageAndPad = Convert.FromHexString(
            "4500003A000000004011142E0C0027110C0027750FA70FA700266DAA001CE00081040D000A0044004D00520020005400450053005400200031000000");
        Assert.Equal(0x76E32A61U, DmrCrc.MessageCrc32(messageAndPad));
        Assert.Equal(0x06C, DmrCrc.ConfirmedBlockCrc9(messageAndPad.AsSpan(0, 16), 0));
    }

    [Fact]
    public void ReservedServiceFrames_AreConsumedWithoutRoutingOrLearning()
    {
        using var fixture = new Fixture();
        var sender = new CountingSender();
        Span<byte> packet = stackalloc byte[53];
        WriteDmrd(packet, 10001, DmrServiceIds.Echo, 1000001, 0xA3);
        fixture.Router.RouteDmrd(packet, fixture.Endpoint, sender);

        Assert.Equal(0, sender.SendCount);
        Assert.Equal(1, fixture.Router.ConsumedServiceFrames);

        WriteDmrd(packet, DmrServiceIds.Echo, 10001, 1000001, 0xE3);
        fixture.Router.RouteDmrd(packet, fixture.Endpoint, sender);
        Assert.Equal(1, fixture.Router.DroppedReservedSources);
        Assert.Equal(0, sender.SendCount);
    }

    [Fact]
    public void EchoService_AcceptsCommandAndSuppressesDuplicateForThirtySeconds()
    {
        using var fixture = new Fixture();
        var settings = ConfigurationTests.ValidSettings() with
        {
            Sds = new SdsSettings { MaxSessions = 4, MaxMessageBytes = 512 }
        };
        using var service = new SdsEchoService(
            NullLogger<SdsEchoService>.Instance, fixture.Repeaters, fixture.Router,
            new CountingSender(), settings);
        var message = new SdsMessageEvent(
            10001, DmrServiceIds.Echo, 1000001, true, 0x04, false, 100, 123_000_100);
        var text = Encoding.Unicode.GetBytes("\r\nECHO Hallo");

        service.HandleMessage(in message, text);
        service.HandleMessage(in message, text);

        Assert.Equal(1, service.AcceptedRequests);
        Assert.Equal(1, service.DuplicateRequests);
    }

    [Fact]
    public async Task EchoService_SendsPrivateConfirmedReplyAndAcceptsRadioAck()
    {
        using var fixture = new Fixture();
        var sender = new AcknowledgingSender(fixture.Router, fixture.Endpoint);
        var settings = ConfigurationTests.ValidSettings() with
        {
            Sds = new SdsSettings { MaxSessions = 4, MaxMessageBytes = 512 }
        };
        using var service = new SdsEchoService(
            NullLogger<SdsEchoService>.Instance, fixture.Repeaters, fixture.Router, sender, settings);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await service.StartAsync(timeout.Token);
        var message = new SdsMessageEvent(
            10001, DmrServiceIds.Echo, 1000001, true, 0x04, false,
            DateTime.UtcNow.AddMilliseconds(-123).Ticks, DateTime.UtcNow.Ticks);

        service.HandleMessage(in message, Encoding.Unicode.GetBytes("\r\nECHO Hallo"));
        while (service.SuccessfulReplies == 0) await Task.Delay(20, timeout.Token);
        await service.StopAsync(timeout.Token);

        Assert.True(sender.SendCount >= 6);
        Assert.Equal(DmrServiceIds.Echo, sender.LastSourceId);
        Assert.Equal(10001, sender.LastDestinationId);
        Assert.True(sender.AllFramesWerePrivate);
        Assert.Equal(1, service.SuccessfulReplies);
        Assert.Equal(0, service.FailedReplies);
    }

    [Fact]
    public void WarmedReservedServiceClassification_AllocatesNoManagedBytes()
    {
        using var fixture = new Fixture();
        var sender = new CountingSender();
        Span<byte> packet = stackalloc byte[53];
        WriteDmrd(packet, 10001, DmrServiceIds.Echo, 1000001, 0xA3);
        for (var i = 0; i < 100; i++) fixture.Router.RouteDmrd(packet, fixture.Endpoint, sender);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) fixture.Router.RouteDmrd(packet, fixture.Endpoint, sender);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    private static void WriteDmrd(Span<byte> packet, int sourceId, int destinationId, int repeaterId, byte flags)
    {
        packet.Clear();
        "DMRD"u8.CopyTo(packet);
        packet[5] = (byte)(sourceId >> 16);
        packet[6] = (byte)(sourceId >> 8);
        packet[7] = (byte)sourceId;
        packet[8] = (byte)(destinationId >> 16);
        packet[9] = (byte)(destinationId >> 8);
        packet[10] = (byte)destinationId;
        BinaryPrimitives.WriteInt32BigEndian(packet[11..15], repeaterId);
        packet[15] = flags;
    }

    private sealed class CountingSender : IDmrSender
    {
        public int SendCount { get; private set; }
        public void SendTo(ReadOnlySpan<byte> packet, Ipv4Endpoint endPoint) => SendCount++;
    }

    private sealed class AcknowledgingSender(MicroSubnetRouter router, Ipv4Endpoint endpoint) : IDmrSender
    {
        private bool _acknowledged;
        public int SendCount { get; private set; }
        public int LastSourceId { get; private set; }
        public int LastDestinationId { get; private set; }
        public bool AllFramesWerePrivate { get; private set; } = true;

        public void SendTo(ReadOnlySpan<byte> packet, Ipv4Endpoint endPoint)
        {
            SendCount++;
            LastSourceId = (packet[5] << 16) | (packet[6] << 8) | packet[7];
            LastDestinationId = (packet[8] << 16) | (packet[9] << 8) | packet[10];
            AllFramesWerePrivate &= (packet[15] & 0x40) != 0;
            if (_acknowledged || (packet[15] & 0x0F) != 0x08) return;
            _acknowledged = true;
            Span<byte> burst = stackalloc byte[33];
            Span<byte> response = stackalloc byte[53];
            DmrBurstWriter.WriteConfirmedResponseHeader(burst, 10001, DmrServiceIds.Echo, 1, true, 1);
            DmrBurstWriter.WriteDmrd(response, burst, 1, 10001, DmrServiceIds.Echo,
                1000001, 0x06, 42);
            router.RouteDmrd(response, endpoint, this);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public RepeaterRegistry Repeaters { get; }
        public MicroSubnetRouter Router { get; }
        public Ipv4Endpoint Endpoint { get; } = new(0x7F000001, 62031);
        private readonly MeshDiscoveryService _mesh;

        public Fixture()
        {
            Repeaters = new RepeaterRegistry(NullLogger<RepeaterRegistry>.Instance, 100, "secret");
            var masters = new MasterRegistry(NullLogger<MasterRegistry>.Instance);
            var roaming = new RoamingRegistry(NullLogger<RoamingRegistry>.Instance, 8);
            _mesh = new MeshDiscoveryService(NullLogger<MeshDiscoveryService>.Instance, masters, roaming,
                100, 62031, 0, "mesh");
            Router = new MicroSubnetRouter(NullLogger<MicroSubnetRouter>.Instance, Repeaters, masters, roaming,
                _mesh, 100, 8, 8);
            Repeaters.TryGet(1000001, out var repeater);
            repeater.EndPoint = Endpoint;
            repeater.State = RepeaterState.LoggedIn;
            Repeaters.RefreshRoutingSnapshot();
        }

        public void Dispose()
        {
            Router.Dispose();
            _mesh.Dispose();
            Repeaters.Dispose();
        }
    }
}
