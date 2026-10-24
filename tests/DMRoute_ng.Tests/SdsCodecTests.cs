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
    private const string CapturedRt3sFallbackAcknowledgement =
        "444D5244000027750F1B92000F6951E607D8BFAE1333147F07B40A01B173C506458D5D7F77FD75733858E10073D62CA04E9A0C3969";

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
    public void ConfirmedResponseParser_AcceptsCapturedRt3sFallbackAcknowledgement()
    {
        var packet = Convert.FromHexString(CapturedRt3sFallbackAcknowledgement);

        Assert.True(DmrBurstWriter.TryReadConfirmedResponse(
            packet, 10101, 990098, out var acknowledged));
        Assert.True(acknowledged);
        Assert.False(DmrBurstWriter.TryReadConfirmedResponse(packet, 10102, 990098, out _));
        Assert.False(DmrBurstWriter.TryReadConfirmedResponse(packet, 10101, 990097, out _));
    }

    [Fact]
    public async Task FallbackSds_AcceptsDmrResponseHeaderAsDeliveryConfirmation()
    {
        using var fixture = new Fixture();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var settings = ConfigurationTests.ValidSettings() with
        {
            RadioCheck = new RadioCheckSettings { FallbackCapacity = 8 }
        };
        var sender = new FallbackAcknowledgingSender(fixture.Router, fixture.Endpoint);
        using var fallback = new FallbackSdsService(
            NullLogger<FallbackSdsService>.Instance, fixture.Router, sender, settings);
        var completion = new TaskCompletionSource<FallbackSdsDelivery>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fallback.DeliveryCompleted += Complete;
        await fallback.StartAsync(timeout.Token);
        var route = new RadioTargetRoute(1000001, fixture.Endpoint, 1, DateTime.UtcNow.Ticks);
        var request = new FallbackSdsRequest(42, 100, 10101, 10001, route);

        Assert.True(fallback.TryQueue(in request));
        var delivery = await completion.Task.WaitAsync(timeout.Token);
        await fallback.StopAsync(timeout.Token);

        Assert.True(delivery.Delivered);
        Assert.Equal(42UL, delivery.MessageId);
        Assert.True(sender.SendCount >= 7);
        return;

        void Complete(in FallbackSdsDelivery result) => completion.TrySetResult(result);
    }

    [Fact]
    public async Task FallbackSds_AcceptsResponseDuringLateGracePeriod()
    {
        using var fixture = new Fixture();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var settings = ConfigurationTests.ValidSettings() with
        {
            RadioCheck = new RadioCheckSettings { FallbackCapacity = 8 }
        };
        var sender = new DelayedFallbackResponseSender(
            fixture.Router,
            fixture.Endpoint,
            TimeSpan.FromMilliseconds(30),
            acknowledged: true,
            responseSourceId: 10001,
            responseDestinationId: DmrServiceIds.ConnectionAttempt);
        using var fallback = new FallbackSdsService(
            NullLogger<FallbackSdsService>.Instance,
            fixture.Router,
            sender,
            settings,
            null,
            FastFallbackTiming());
        var completion = new TaskCompletionSource<FallbackSdsDelivery>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fallback.DeliveryCompleted += Complete;
        await fallback.StartAsync(timeout.Token);
        var route = new RadioTargetRoute(1000001, fixture.Endpoint, 1, DateTime.UtcNow.Ticks);
        var request = new FallbackSdsRequest(43, 100, 10101, 10001, route);

        Assert.True(fallback.TryQueue(in request));
        var delivery = await completion.Task.WaitAsync(timeout.Token);
        await sender.ResponseSent.WaitAsync(timeout.Token);
        await fallback.StopAsync(timeout.Token);

        Assert.True(delivery.Delivered);
        Assert.Equal(3, sender.HeaderCount);
        return;

        void Complete(in FallbackSdsDelivery result) => completion.TrySetResult(result);
    }

    [Theory]
    [InlineData(false, 10001, DmrServiceIds.ConnectionAttempt)]
    [InlineData(true, 10002, DmrServiceIds.ConnectionAttempt)]
    [InlineData(true, 10001, 990097)]
    public async Task FallbackSds_RejectsLateNackOrMismatchedResponse(
        bool acknowledged,
        int responseSourceId,
        int responseDestinationId)
    {
        using var fixture = new Fixture();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var settings = ConfigurationTests.ValidSettings() with
        {
            RadioCheck = new RadioCheckSettings { FallbackCapacity = 8 }
        };
        var sender = new DelayedFallbackResponseSender(
            fixture.Router,
            fixture.Endpoint,
            TimeSpan.FromMilliseconds(30),
            acknowledged,
            responseSourceId,
            responseDestinationId);
        using var fallback = new FallbackSdsService(
            NullLogger<FallbackSdsService>.Instance,
            fixture.Router,
            sender,
            settings,
            null,
            FastFallbackTiming());
        var completion = new TaskCompletionSource<FallbackSdsDelivery>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fallback.DeliveryCompleted += Complete;
        await fallback.StartAsync(timeout.Token);
        var route = new RadioTargetRoute(1000001, fixture.Endpoint, 1, DateTime.UtcNow.Ticks);
        var request = new FallbackSdsRequest(44, 100, 10101, 10001, route);

        Assert.True(fallback.TryQueue(in request));
        var delivery = await completion.Task.WaitAsync(timeout.Token);
        await sender.ResponseSent.WaitAsync(timeout.Token);
        await fallback.StopAsync(timeout.Token);

        Assert.False(delivery.Delivered);
        Assert.Equal(3, sender.HeaderCount);
        return;

        void Complete(in FallbackSdsDelivery result) => completion.TrySetResult(result);
    }

    [Fact]
    public async Task FallbackSds_FailsAfterLateGraceWithoutResponse()
    {
        using var fixture = new Fixture();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var settings = ConfigurationTests.ValidSettings() with
        {
            RadioCheck = new RadioCheckSettings { FallbackCapacity = 8 }
        };
        var sender = new CountingSender();
        using var fallback = new FallbackSdsService(
            NullLogger<FallbackSdsService>.Instance,
            fixture.Router,
            sender,
            settings,
            null,
            FastFallbackTiming());
        var completion = new TaskCompletionSource<FallbackSdsDelivery>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fallback.DeliveryCompleted += Complete;
        await fallback.StartAsync(timeout.Token);
        var route = new RadioTargetRoute(1000001, fixture.Endpoint, 1, DateTime.UtcNow.Ticks);
        var request = new FallbackSdsRequest(45, 100, 10101, 10001, route);

        Assert.True(fallback.TryQueue(in request));
        var delivery = await completion.Task.WaitAsync(timeout.Token);
        await fallback.StopAsync(timeout.Token);

        Assert.False(delivery.Delivered);
        Assert.True(sender.SendCount > 0);
        return;

        void Complete(in FallbackSdsDelivery result) => completion.TrySetResult(result);
    }

    private static FallbackSdsTiming FastFallbackTiming() => new(
        TimeSpan.FromMilliseconds(1),
        TimeSpan.FromMilliseconds(10),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(1));

    [Fact]
    public void ControlWriters_ReproduceCapturedCallAlertAndRadioCheckBursts()
    {
        Span<byte> burst = stackalloc byte[DmrBurstWriter.BurstLength];

        DmrBurstWriter.WriteCallAlertRequest(burst, 10001, 10101, 1);
        Assert.Equal(
            "04628759063A22040008507084CD5D7F77FD757ACBDD1528222843643ED9159305",
            Convert.ToHexString(burst));

        DmrBurstWriter.WriteCallAlertAcknowledgement(burst, 10101, 10001, 1);
        Assert.Equal(
            "44BF01A887FC2F7134D254A404CD5D7F77FD757ACB3B946987C8068433C1388341",
            Convert.ToHexString(burst));

        DmrBurstWriter.WriteRadioCheckRequest(burst, 10001, 10101, 1);
        Assert.Equal(
            "04DD81B006C42D2C341059C004CD5D7F77FD757ACBAF144807E800043E49289364",
            Convert.ToHexString(burst));

        DmrBurstWriter.WriteRadioCheckAcknowledgement(burst, 10101, 10001, 1);
        Assert.Equal(
            "0493813A0650AE2C3620558004CD5D7F77FD757ACB2F1540859800463B09301373",
            Convert.ToHexString(burst));

        DmrBurstWriter.WriteCsbkPreamble(burst, 10001, 10101, 5, 1, false);
        Span<byte> decoded = stackalloc byte[12];
        Bptc19696.Decode(burst, decoded);
        Assert.Equal("BD000005002775002711", Convert.ToHexString(decoded[..10]));
        Assert.True(DmrCrc.CheckMaskedCcitt(decoded, 0xA5A5));
    }

    [Fact]
    public void ControlParser_RecognizesCapturedMessagesAndRejectsInconsistentFrames()
    {
        Span<byte> burst = stackalloc byte[DmrBurstWriter.BurstLength];
        Span<byte> packet = stackalloc byte[DmrBurstWriter.DmrdLength];

        DmrBurstWriter.WriteCallAlertRequest(burst, 10001, 10101, 1);
        DmrBurstWriter.WriteDmrd(packet, burst, 1, 10001, 10101, 1000001, 0x03, 1);
        Assert.True(DmrBurstWriter.TryReadControlMessage(packet, out var callAlert));
        Assert.Equal(
            new DmrControlMessage(DmrControlMessageType.CallAlertRequest, 10001, 10101),
            callAlert);

        DmrBurstWriter.WriteCallAlertAcknowledgement(burst, 10101, 10001, 1);
        DmrBurstWriter.WriteDmrd(packet, burst, 2, 10101, 10001, 1000001, 0x03, 2);
        Assert.True(DmrBurstWriter.TryReadControlMessage(packet, out var callAlertAck));
        Assert.Equal(DmrControlMessageType.CallAlertAcknowledgement, callAlertAck.Type);

        DmrBurstWriter.WriteRadioCheckRequest(burst, 10001, 10101, 1);
        DmrBurstWriter.WriteDmrd(packet, burst, 3, 10001, 10101, 1000001, 0x03, 3);
        Assert.True(DmrBurstWriter.TryReadControlMessage(packet, out var radioCheck));
        Assert.Equal(DmrControlMessageType.RadioCheckRequest, radioCheck.Type);

        DmrBurstWriter.WriteRadioCheckAcknowledgement(burst, 10101, 10001, 1);
        DmrBurstWriter.WriteDmrd(packet, burst, 4, 10101, 10001, 1000001, 0x03, 4);
        Assert.True(DmrBurstWriter.TryReadControlMessage(packet, out var radioCheckAck));
        Assert.Equal(DmrControlMessageType.RadioCheckAcknowledgement, radioCheckAck.Type);

        packet[5] ^= 1;
        Assert.False(DmrBurstWriter.TryReadControlMessage(packet, out _));
        packet[5] ^= 1;
        packet[15] &= unchecked((byte)~0x40);
        Assert.False(DmrBurstWriter.TryReadControlMessage(packet, out _));

        Span<byte> invalid = stackalloc byte[12];
        Bptc19696.Decode(burst, invalid);
        invalid[3] ^= 1;
        Bptc19696.Encode(invalid, burst);
        DmrBurstWriter.WriteDmrd(packet, burst, 5, 10101, 10001, 1000001, 0x03, 5);
        Assert.False(DmrBurstWriter.TryReadControlMessage(packet, out _));

        DmrBurstWriter.WriteRadioCheckRequest(burst, 10001, 10101, 1);
        Bptc19696.Decode(burst, invalid);
        invalid[1] = 0x11;
        DmrCrc.WriteMaskedCcitt(invalid[10..12], invalid[..10], 0xA5A5);
        Bptc19696.Encode(invalid, burst);
        DmrBurstWriter.WriteDmrd(packet, burst, 6, 10001, 10101, 1000001, 0x03, 6);
        Assert.False(DmrBurstWriter.TryReadControlMessage(packet, out _));
    }

    [Fact]
    public void WarmedControlParser_AllocatesNoManagedBytes()
    {
        Span<byte> burst = stackalloc byte[DmrBurstWriter.BurstLength];
        Span<byte> packet = stackalloc byte[DmrBurstWriter.DmrdLength];
        DmrBurstWriter.WriteRadioCheckRequest(burst, 10001, 10101, 1);
        DmrBurstWriter.WriteDmrd(packet, burst, 1, 10001, 10101, 1000001, 0x03, 1);
        for (var i = 0; i < 100; i++)
            Assert.True(DmrBurstWriter.TryReadControlMessage(packet, out _));

        var parsed = 0;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
            if (DmrBurstWriter.TryReadControlMessage(packet, out _)) parsed++;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(1000, parsed);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void DeliveryReportWriter_ReproducesCapturedFuGBursts()
    {
        Span<byte> datagram = stackalloc byte[64];
        Assert.True(TmsMessageWriter.TryWriteDeliveryReport(
            datagram, 10003, 10001, 0x0013, out var datagramLength));
        Assert.Equal(33, datagramLength);
        Assert.Equal(
            "4500002100130000401114960C0027130C0027110FA70FA7000DBA5E0003BF0001",
            Convert.ToHexString(datagram[..datagramLength]));

        var ipv4 = new Ipv4Packet(datagram[..datagramLength]);
        Assert.True(ipv4.IsValid);
        var udp = new UdpDatagram(ipv4.Payload);
        Assert.True(udp.IsValid);
        Assert.True(TmsDeliveryReport.TryRead(udp.Payload, out var status));
        Assert.Equal(TmsDeliveryStatus.Delivered, status);

        Span<byte> header = stackalloc byte[DmrBurstWriter.BurstLength];
        DmrBurstWriter.WriteConfirmedDataHeader(header, 10003, 10001, 3, 11, 0, 1);
        Assert.Equal(
            "0393342101570A72210173CA058D5D7F77FD75733AB00034712409C30CCC0B21FD",
            Convert.ToHexString(header));

        Span<byte> fragment = stackalloc byte[48];
        datagram[..datagramLength].CopyTo(fragment);
        var crc = DmrCrc.MessageCrc32(fragment[..44]);
        BinaryPrimitives.WriteUInt32LittleEndian(fragment[44..], crc);
        var expectedBursts = new[]
        {
            "2132E12FE8F9624222B22862063D5D7F77FD757A87E3272F2ECD235820D2751BE9",
            "20F9E91290252232313C2FB2C63D5D7F77FD757A867D23502D224F3BE52D7452D2",
            "20E2222222B9ADE2222222EA063D5D7F77FD757A847F22222229FF0D22222229E6"
        };
        Span<byte> burst = stackalloc byte[DmrBurstWriter.BurstLength];
        for (byte block = 0; block < 3; block++)
        {
            DmrBurstWriter.WriteConfirmedRateThreeQuarterBlock(
                burst, fragment.Slice(block * 16, 16), block, 1);
            Assert.Equal(expectedBursts[block], Convert.ToHexString(burst));
        }
    }

    [Fact]
    public void DeliveryReportParser_RejectsUnknownTmsPayload()
    {
        Assert.False(TmsDeliveryReport.TryRead([0x00, 0x03, 0xBF, 0x00, 0x00], out _));
        Assert.False(TmsDeliveryReport.TryRead([0x00, 0x03, 0xBF, 0x00], out _));
    }

    [Fact]
    public void UnconfirmedGroupWriters_RoundTripHeaderBlockAndOuterFlags()
    {
        Span<byte> header = stackalloc byte[DmrBurstWriter.BurstLength];
        DmrBurstWriter.WriteUnconfirmedDataHeader(header, 10001, 9, 2, 16, true, 1);
        Span<byte> decodedHeader = stackalloc byte[12];
        Bptc19696.Decode(header, decodedHeader);
        Assert.Equal(0x02, decodedHeader[0] & 0x0F);
        Assert.NotEqual(0, decodedHeader[0] & 0x80);
        Assert.NotEqual(0, decodedHeader[0] & 0x10);
        Assert.Equal(0, decodedHeader[1] & 0x0F);
        Assert.True(DmrCrc.CheckMaskedCcitt(decodedHeader, 0xCCCC));

        Span<byte> data = stackalloc byte[18];
        for (var index = 0; index < data.Length; index++) data[index] = (byte)index;
        Span<byte> burst = stackalloc byte[DmrBurstWriter.BurstLength];
        DmrBurstWriter.WriteUnconfirmedRateThreeQuarterBlock(burst, data, 1);
        Span<byte> decodedData = stackalloc byte[18];
        Assert.True(DmrTrellis.Decode(burst, decodedData));
        Assert.True(data.SequenceEqual(decodedData));

        Span<byte> packet = stackalloc byte[DmrBurstWriter.DmrdLength];
        DmrBurstWriter.WriteDmrd(packet, burst, 1, 10001, 9, 1000001, 0x08, 42, 1, true);
        Assert.True(DMRoute_ng.Emulator.HomebrewProtocol.TryReadDmrdInfo(packet, out var info));
        Assert.Equal(1, info.Timeslot);
        Assert.True(info.IsGroupCall);
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

    private sealed class FallbackAcknowledgingSender(MicroSubnetRouter router, Ipv4Endpoint endpoint) : IDmrSender
    {
        private bool _acknowledged;
        public int SendCount { get; private set; }

        public void SendTo(ReadOnlySpan<byte> packet, Ipv4Endpoint endPoint)
        {
            SendCount++;
            if (_acknowledged || (packet[15] & 0x0F) != 0x08) return;
            _acknowledged = true;
            Span<byte> burst = stackalloc byte[DmrBurstWriter.BurstLength];
            Span<byte> response = stackalloc byte[DmrBurstWriter.DmrdLength];
            DmrBurstWriter.WriteConfirmedResponseHeader(
                burst, 10001, DmrServiceIds.ConnectionAttempt, 1, true, 1);
            DmrBurstWriter.WriteDmrd(
                response, burst, 1, 10001, DmrServiceIds.ConnectionAttempt, 1000001, 0x06, 42);
            router.RouteDmrd(response, endpoint, this);
        }
    }

    private sealed class DelayedFallbackResponseSender(
        MicroSubnetRouter router,
        Ipv4Endpoint endpoint,
        TimeSpan delay,
        bool acknowledged,
        int responseSourceId,
        int responseDestinationId) : IDmrSender
    {
        private readonly TaskCompletionSource _responseSent =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _dataBlockCount;
        private int _responseScheduled;

        public int HeaderCount { get; private set; }
        public Task ResponseSent => _responseSent.Task;

        public void SendTo(ReadOnlySpan<byte> packet, Ipv4Endpoint endPoint)
        {
            var dataType = packet[15] & 0x0F;
            if (dataType == 0x06) HeaderCount++;
            if (dataType != 0x08 || Interlocked.Increment(ref _dataBlockCount) != 21 ||
                Interlocked.Exchange(ref _responseScheduled, 1) != 0) return;
            _ = SendResponseAsync();
        }

        private async Task SendResponseAsync()
        {
            await Task.Delay(delay);
            Span<byte> burst = stackalloc byte[DmrBurstWriter.BurstLength];
            Span<byte> response = stackalloc byte[DmrBurstWriter.DmrdLength];
            DmrBurstWriter.WriteConfirmedResponseHeader(
                burst, responseSourceId, responseDestinationId, 1, acknowledged, 1);
            DmrBurstWriter.WriteDmrd(
                response,
                burst,
                1,
                responseSourceId,
                responseDestinationId,
                1000001,
                0x06,
                42);
            router.RouteDmrd(response, endpoint, this);
            _responseSent.TrySetResult();
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
