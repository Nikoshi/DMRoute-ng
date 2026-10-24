using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using DMRoute_ng.Coding;
using DMRoute_ng.Configuration;
using DMRoute_ng.Core;
using DMRoute_ng.Emulator;
using DMRoute_ng.Gateways;
using DMRoute_ng.Playground;
using DMRoute_ng.Registry;
using DMRoute_ng.Routing;
using DMRoute_ng.Services;
using DMRoute_ng.Types;
using Microsoft.Extensions.Logging.Abstractions;

namespace DMRoute_ng.Tests;

public sealed class HomebrewEmulatorTests
{
    private static readonly byte[] CapturedGroupDataFrame = Convert.FromHexString(
        "444D5244050027110F1B93000F4241A6A3E1BE9413ED97E6073B088BA733CF8F058D5D7F77FD757338C1C05A79680661090210199A");

    [Fact]
    public void VirtualRadio_DefaultRetryWaitMatchesHardware()
    {
        var options = new VirtualRadioOptions { RadioId = 10001 };
        Assert.Equal(TimeSpan.FromMilliseconds(2500), options.ResponseTimeout);
    }

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
    public void DmrdInspector_ReadsOuterFrameFields()
    {
        Assert.True(HomebrewProtocol.TryReadDmrdInfo(CapturedGroupDataFrame, out var info));

        Assert.Equal(5, info.SequenceNumber);
        Assert.Equal(10001, info.SourceId);
        Assert.Equal(990099, info.DestinationId);
        Assert.Equal(1000001, info.RepeaterId);
        Assert.Equal(2, info.Timeslot);
        Assert.True(info.IsGroupCall);
        Assert.True(info.IsDataFrame);
        Assert.Equal(0x06, info.DataType);
        Assert.Equal(0xA3E1BE94u, info.StreamId);
        Assert.False(HomebrewProtocol.TryReadDmrdInfo("DMRD"u8, out _));
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

        var routedFrame = CapturedGroupDataFrame.ToArray();
        routedFrame[8] = 0;
        routedFrame[9] = 0;
        routedFrame[10] = 9;
        var scenario = new RadioScenario(
            "captured fixed-position group data",
            10001,
            [new RadioScenarioStep(routedFrame, TimeSpan.Zero)]);
        var radio = new VirtualRadio(10001);
        await radio.ReplayAsync(source, scenario, timeout.Token);

        var forwarded = await target.ReceiveDmrdAsync(timeout.Token);
        Assert.Equal(routedFrame, forwarded.Payload);
        await Assert.ThrowsAsync<HomebrewTimeoutException>(
            () => source.ReceiveDmrdAsync(TimeSpan.FromMilliseconds(150), timeout.Token));
    }

    [Fact]
    public async Task VirtualRadios_SendAndReceiveGroupSdsOverTwoHotspots()
    {
        await using var fixture = await DmrLoopbackFixture.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var sourceHotspot = new VirtualHotspot(CreateOptions(fixture.EndPoint, 1000001));
        await using var targetHotspot = new VirtualHotspot(CreateOptions(fixture.EndPoint, 1000002) with
        {
            ReceiveQueueCapacity = 1
        });
        await using var source = new VirtualRadio(sourceHotspot, new VirtualRadioOptions { RadioId = 10001 });
        await using var target = new VirtualRadio(targetHotspot, new VirtualRadioOptions
        {
            RadioId = 10002,
            Talkgroups = [9]
        });
        await using var nonMember = new VirtualRadio(targetHotspot, new VirtualRadioOptions
        {
            RadioId = 10003,
            Talkgroups = [10]
        });
        await sourceHotspot.LoginAsync(timeout.Token);
        await targetHotspot.LoginAsync(timeout.Token);

        var result = await source.SendSdsAsync(SdsTargetKind.Talkgroup, 9, "Padding", timeout.Token);
        var received = await target.ReceiveSdsAsync(timeout.Token);

        Assert.Equal(SdsSendStatus.Sent, result.Status);
        Assert.Equal(10001, received.SourceId);
        Assert.Equal(9, received.DestinationId);
        Assert.True(received.IsGroupCall);
        Assert.False(received.IsConfirmed);
        Assert.Equal("Padding", received.Text);
        Assert.False(nonMember.TryReceiveSds(out _));
        var raw = await targetHotspot.ReceiveDmrdAsync(timeout.Token);
        Assert.True(HomebrewProtocol.TryReadDmrdInfo(raw.Payload, out _));
        Assert.True(targetHotspot.DroppedDmrdPackets > 0);
        Assert.Equal(VirtualHotspotState.Configured, targetHotspot.State);
    }

    [Fact]
    public async Task VirtualRadios_AnnounceAndAcknowledgePrivateSds()
    {
        await using var fixture = await DmrLoopbackFixture.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var sourceHotspot = new VirtualHotspot(CreateOptions(fixture.EndPoint, 1000001) with
        {
            ReceiveQueueCapacity = 32
        });
        await using var targetHotspot = new VirtualHotspot(CreateOptions(fixture.EndPoint, 1000002) with
        {
            ReceiveQueueCapacity = 32
        });
        await using var source = new VirtualRadio(sourceHotspot, new VirtualRadioOptions { RadioId = 10001 });
        await using var target = new VirtualRadio(targetHotspot, new VirtualRadioOptions { RadioId = 10002 });
        await sourceHotspot.LoginAsync(timeout.Token);
        await targetHotspot.LoginAsync(timeout.Token);
        await target.AnnounceAsync(timeout.Token);
        var preambles = 0;
        targetHotspot.DmrdReceived += CountSourcePreamble;

        var result = await source.SendSdsAsync(SdsTargetKind.Radio, 10002, "Direkte SDS", timeout.Token);
        var received = await target.ReceiveSdsAsync(timeout.Token);

        Assert.True(target.IsAnnounced);
        Assert.Equal(SdsSendStatus.Acknowledged, result.Status);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(10001, received.SourceId);
        Assert.Equal(10002, received.DestinationId);
        Assert.False(received.IsGroupCall);
        Assert.True(received.IsConfirmed);
        Assert.Equal("Direkte SDS", received.Text);
        Assert.Equal(5, Volatile.Read(ref preambles));
        return;

        void CountSourcePreamble(HomebrewDatagram datagram)
        {
            if (HomebrewProtocol.TryReadDmrdInfo(datagram.Payload, out var info) &&
                info.SourceId == 10001 && info.DestinationId == 10002 && info.DataType == 0x03)
                Interlocked.Increment(ref preambles);
        }
    }

    [Fact]
    public async Task VirtualRadios_RoundTripCallAlertAndRadioCheck()
    {
        await using var fixture = await DmrLoopbackFixture.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var sourceHotspot = new VirtualHotspot(CreateOptions(fixture.EndPoint, 1000001) with
        {
            ReceiveQueueCapacity = 64
        });
        await using var targetHotspot = new VirtualHotspot(CreateOptions(fixture.EndPoint, 1000002) with
        {
            ReceiveQueueCapacity = 64
        });
        await using var source = new VirtualRadio(sourceHotspot, new VirtualRadioOptions { RadioId = 10001 });
        await using var target = new VirtualRadio(targetHotspot, new VirtualRadioOptions { RadioId = 10002 });
        await sourceHotspot.LoginAsync(timeout.Token);
        await targetHotspot.LoginAsync(timeout.Token);
        await target.AnnounceAsync(timeout.Token);

        var callAlertFrames = 0;
        var radioCheckFrames = 0;
        var countRadioCheck = false;
        targetHotspot.DmrdReceived += CountControlFrames;

        var alertResult = await source.SendCallAlertAsync(10002, timeout.Token);
        var alertRequest = await target.ReceiveControlEventAsync(timeout.Token);
        var alertAck = await source.ReceiveControlEventAsync(timeout.Token);
        countRadioCheck = true;
        var checkResult = await source.CheckRadioAsync(10002, timeout.Token);
        var checkRequest = await target.ReceiveControlEventAsync(timeout.Token);
        var checkAck = await source.ReceiveControlEventAsync(timeout.Token);

        Assert.Equal(new RadioControlSendResult(RadioControlSendStatus.Acknowledged, 1), alertResult);
        Assert.Equal(RadioControlEventType.CallAlertRequest, alertRequest.Type);
        Assert.Equal(RadioControlEventType.CallAlertAcknowledgement, alertAck.Type);
        Assert.Equal(new RadioControlSendResult(RadioControlSendStatus.Acknowledged, 1), checkResult);
        Assert.Equal(RadioControlEventType.RadioCheckRequest, checkRequest.Type);
        Assert.Equal(RadioControlEventType.RadioCheckAcknowledgement, checkAck.Type);
        Assert.Equal(10001, checkRequest.SourceId);
        Assert.Equal(10002, checkRequest.DestinationId);
        Assert.Equal(5, Volatile.Read(ref callAlertFrames));
        Assert.Equal(6, Volatile.Read(ref radioCheckFrames));
        return;

        void CountControlFrames(HomebrewDatagram datagram)
        {
            if (!HomebrewProtocol.TryReadDmrdInfo(datagram.Payload, out var info) ||
                info.SourceId != 10001 || info.DestinationId != 10002 || info.DataType != 0x03)
                return;
            if (Volatile.Read(ref countRadioCheck)) Interlocked.Increment(ref radioCheckFrames);
            else Interlocked.Increment(ref callAlertFrames);
        }
    }

    [Fact]
    public async Task VirtualRadio_ControlRequestsRetryAndReportTimeout()
    {
        await using var fixture = await DmrLoopbackFixture.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var hotspot = new VirtualHotspot(CreateOptions(fixture.EndPoint, 1000001));
        await using var radio = new VirtualRadio(hotspot, new VirtualRadioOptions
        {
            RadioId = 10001,
            MaximumAttempts = 2,
            ResponseTimeout = TimeSpan.FromMilliseconds(50)
        });
        await hotspot.LoginAsync(timeout.Token);

        var result = await radio.CheckRadioAsync(10099, timeout.Token);

        Assert.Equal(RadioControlSendStatus.TimedOut, result.Status);
        Assert.Equal(2, result.Attempts);
    }

    [Fact]
    public async Task VirtualRadio_IgnoresControlRequestsOnAnotherTimeslot()
    {
        await using var fixture = await DmrLoopbackFixture.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var sourceHotspot = new VirtualHotspot(CreateOptions(fixture.EndPoint, 1000001));
        await using var targetHotspot = new VirtualHotspot(CreateOptions(fixture.EndPoint, 1000002));
        await using var source = new VirtualRadio(sourceHotspot, new VirtualRadioOptions
        {
            RadioId = 10001,
            Timeslot = 1,
            MaximumAttempts = 1,
            ResponseTimeout = TimeSpan.FromMilliseconds(50)
        });
        await using var target = new VirtualRadio(targetHotspot, new VirtualRadioOptions
        {
            RadioId = 10002,
            Timeslot = 2
        });
        await sourceHotspot.LoginAsync(timeout.Token);
        await targetHotspot.LoginAsync(timeout.Token);
        await target.AnnounceAsync(timeout.Token);

        var result = await source.SendCallAlertAsync(10002, timeout.Token);

        Assert.Equal(RadioControlSendStatus.TimedOut, result.Status);
        Assert.Equal(0, target.UnreadControlEvents);
    }

    [Fact]
    public async Task VirtualRadio_ControlInboxDropsOldestEventWhenFull()
    {
        await using var fixture = await DmrLoopbackFixture.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var sourceHotspot = new VirtualHotspot(CreateOptions(fixture.EndPoint, 1000001));
        await using var targetHotspot = new VirtualHotspot(CreateOptions(fixture.EndPoint, 1000002));
        await using var source = new VirtualRadio(sourceHotspot, new VirtualRadioOptions { RadioId = 10001 });
        await using var target = new VirtualRadio(targetHotspot, new VirtualRadioOptions
        {
            RadioId = 10002,
            InboxCapacity = 1
        });
        await sourceHotspot.LoginAsync(timeout.Token);
        await targetHotspot.LoginAsync(timeout.Token);
        await target.AnnounceAsync(timeout.Token);

        await source.SendCallAlertAsync(10002, timeout.Token);
        await source.CheckRadioAsync(10002, timeout.Token);

        Assert.Equal(1, target.UnreadControlEvents);
        Assert.Equal(1, target.DroppedControlEvents);
        Assert.True(target.TryReceiveControlEvent(out var latest));
        Assert.Equal(RadioControlEventType.RadioCheckRequest, latest.Type);
        Assert.Equal(0, target.ClearControlEvents());
    }

    [Fact]
    public async Task WarmedVirtualRadioControlIngestion_AllocatesNoManagedBytes()
    {
        var radio = new VirtualRadio(10001);
        try
        {
            var method = typeof(VirtualRadio).GetMethod(
                "HandleDmrd", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(method);
            var handle = (Action<HomebrewDatagram>)method.CreateDelegate(typeof(Action<HomebrewDatagram>), radio);
            var packet = BuildRadioCheckAcknowledgement();
            var datagram = new HomebrewDatagram(packet, DateTimeOffset.UnixEpoch);
            for (var i = 0; i < 100; i++) handle(datagram);

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++) handle(datagram);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(0, allocated);
            Assert.Equal(1, radio.UnreadControlEvents);
        }
        finally
        {
            await radio.DisposeAsync();
        }
    }

    [Fact]
    public async Task VirtualRadio_PrivateSdsRetriesAndReportsTimeout()
    {
        await using var fixture = await DmrLoopbackFixture.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var hotspot = new VirtualHotspot(CreateOptions(fixture.EndPoint, 1000001));
        await using var radio = new VirtualRadio(hotspot, new VirtualRadioOptions
        {
            RadioId = 10001,
            MaximumAttempts = 2,
            ResponseTimeout = TimeSpan.FromMilliseconds(50)
        });
        await hotspot.LoginAsync(timeout.Token);

        var result = await radio.SendSdsAsync(SdsTargetKind.Radio, 10099, "Niemand da", timeout.Token);

        Assert.Equal(SdsSendStatus.TimedOut, result.Status);
        Assert.Equal(2, result.Attempts);
    }

    [Fact]
    public async Task VirtualRadio_CompletesEchoServiceTransaction()
    {
        await using var fixture = await DmrLoopbackFixture.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var gateway = new SdsGateway(
            NullLogger<SdsGateway>.Instance, fixture.Router, 8, 512);
        using var echo = new SdsEchoService(
            NullLogger<SdsEchoService>.Instance, fixture.Repeaters, fixture.Router,
            fixture.Sender, ConfigurationTests.ValidSettings() with
            {
                Sds = new SdsSettings { MaxSessions = 8, MaxMessageBytes = 512 }
            });
        using var services = new SdsServiceRegistry(gateway, [echo]);
        await echo.StartAsync(timeout.Token);
        await using var hotspot = new VirtualHotspot(CreateOptions(fixture.EndPoint, 1000001) with
        {
            ReceiveQueueCapacity = 32
        });
        await using var radio = new VirtualRadio(hotspot, new VirtualRadioOptions { RadioId = 10001 });
        await hotspot.LoginAsync(timeout.Token);

        await radio.SendSdsAsync(SdsTargetKind.Talkgroup, DmrServiceIds.Echo, "ECHO Playground", timeout.Token);
        var reply = await radio.ReceiveSdsAsync(timeout.Token);
        await WaitUntilAsync(() => echo.SuccessfulReplies == 1, timeout.Token);
        await echo.StopAsync(timeout.Token);

        Assert.Equal(DmrServiceIds.Echo, reply.SourceId);
        Assert.Equal(10001, reply.DestinationId);
        Assert.StartsWith("ECHO Playground | RX ", reply.Text, StringComparison.Ordinal);
        Assert.EndsWith(" ms", reply.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VirtualRadio_ReceivesAndConfirmsFallbackConnectionAttempt()
    {
        await using var fixture = await DmrLoopbackFixture.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var settings = ConfigurationTests.ValidSettings() with
        {
            Sds = new SdsSettings { MaxSessions = 8, MaxMessageBytes = 512 },
            RadioCheck = new RadioCheckSettings { FallbackCapacity = 8 }
        };
        var gateway = new SdsGateway(
            NullLogger<SdsGateway>.Instance, fixture.Router, 8, 512);
        using var fallback = new FallbackSdsService(
            NullLogger<FallbackSdsService>.Instance, fixture.Router, fixture.Sender, settings);
        using var services = new SdsServiceRegistry(gateway, [fallback]);
        await fallback.StartAsync(timeout.Token);
        await using var hotspot = new VirtualHotspot(CreateOptions(fixture.EndPoint, 1000001));
        await using var radio = new VirtualRadio(hotspot, new VirtualRadioOptions { RadioId = 10001 });
        await hotspot.LoginAsync(timeout.Token);
        await radio.AnnounceAsync(timeout.Token);
        Assert.True(fixture.Router.TryGetAnyLocalTarget(10001, out var route));
        var completion = new TaskCompletionSource<FallbackSdsDelivery>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fallback.DeliveryCompleted += Complete;
        var request = new FallbackSdsRequest(42, 100, 10101, 10001, route);

        Assert.True(fallback.TryQueue(in request));
        var message = await radio.ReceiveSdsAsync(timeout.Token);
        var delivery = await completion.Task.WaitAsync(timeout.Token);
        await fallback.StopAsync(timeout.Token);

        Assert.Equal(settings.RadioCheck.FallbackServiceId, message.SourceId);
        Assert.Equal(10001, message.DestinationId);
        Assert.Equal("Verbindungsversuch von 10101", message.Text);
        Assert.Equal(42UL, delivery.MessageId);
        Assert.True(delivery.Delivered);
        return;

        void Complete(in FallbackSdsDelivery result) => completion.TrySetResult(result);
    }

    [Fact]
    public async Task UnknownHomeRadio_IsFoundByBlindProbeWithoutFallback()
    {
        var directory = ConfigurationTests.CreateTemporaryDirectory();
        try
        {
            await using var fixture = await DmrLoopbackFixture.StartAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var settings = CreateRadioCheckSettings(directory);
            var outbox = new RadioCheckOutbox(
                NullLogger<RadioCheckOutbox>.Instance,
                settings.RadioCheck.OutboxPath,
                settings.RadioCheck.FallbackCapacity,
                DateTime.UtcNow.Ticks);
            var gateway = new SdsGateway(NullLogger<SdsGateway>.Instance, fixture.Router, 8, 512);
            using var fallback = new FallbackSdsService(
                NullLogger<FallbackSdsService>.Instance, fixture.Router, fixture.Sender, settings);
            using var services = new SdsServiceRegistry(gateway, [fallback]);
            using var coordinator = new RadioCheckReachabilityCoordinator(
                NullLogger<RadioCheckReachabilityCoordinator>.Instance,
                fixture.Router,
                fixture.Repeaters,
                fixture.Masters,
                fixture.Roaming,
                fixture.Mesh,
                fixture.Sender,
                fallback,
                outbox,
                settings);
            await fallback.StartAsync(timeout.Token);
            await coordinator.StartAsync(timeout.Token);
            try
            {
                await using var sourceHotspot = new VirtualHotspot(CreateOptions(fixture.EndPoint, 1000001));
                await using var targetHotspot = new VirtualHotspot(CreateOptions(fixture.EndPoint, 1000002));
                await using var source = new VirtualRadio(sourceHotspot, new VirtualRadioOptions { RadioId = 10001 });
                await using var target = new VirtualRadio(targetHotspot, new VirtualRadioOptions { RadioId = 10002 });
                await sourceHotspot.LoginAsync(timeout.Token);
                await targetHotspot.LoginAsync(timeout.Token);
                await source.AnnounceAsync(timeout.Token);
                var targetControlFrames = 0;
                targetHotspot.DmrdReceived += CountControlFrames;

                var result = await source.CheckRadioAsync(10002, timeout.Token);
                await WaitUntilAsync(() => Volatile.Read(ref targetControlFrames) >= 6, timeout.Token);

                Assert.Equal(new RadioControlSendResult(RadioControlSendStatus.Acknowledged, 1), result);
                Assert.Equal(6, Volatile.Read(ref targetControlFrames));
                Assert.Equal(0, outbox.Count);

                void CountControlFrames(HomebrewDatagram datagram)
                {
                    if (HomebrewProtocol.TryReadDmrdInfo(datagram.Payload, out var info) && info.DataType == 0x03)
                        Interlocked.Increment(ref targetControlFrames);
                }
            }
            finally
            {
                await coordinator.StopAsync(CancellationToken.None);
                await fallback.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FailedBlindProbe_QueuesOneFallbackAndDeliversAfterTargetAppears()
    {
        var directory = ConfigurationTests.CreateTemporaryDirectory();
        try
        {
            await using var fixture = await DmrLoopbackFixture.StartAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var settings = CreateRadioCheckSettings(directory);
            var outbox = new RadioCheckOutbox(
                NullLogger<RadioCheckOutbox>.Instance,
                settings.RadioCheck.OutboxPath,
                settings.RadioCheck.FallbackCapacity,
                DateTime.UtcNow.Ticks);
            var gateway = new SdsGateway(NullLogger<SdsGateway>.Instance, fixture.Router, 8, 512);
            using var fallback = new FallbackSdsService(
                NullLogger<FallbackSdsService>.Instance, fixture.Router, fixture.Sender, settings);
            using var services = new SdsServiceRegistry(gateway, [fallback]);
            using var coordinator = new RadioCheckReachabilityCoordinator(
                NullLogger<RadioCheckReachabilityCoordinator>.Instance,
                fixture.Router,
                fixture.Repeaters,
                fixture.Masters,
                fixture.Roaming,
                fixture.Mesh,
                fixture.Sender,
                fallback,
                outbox,
                settings);
            await fallback.StartAsync(timeout.Token);
            await coordinator.StartAsync(timeout.Token);
            try
            {
                await using var sourceHotspot = new VirtualHotspot(CreateOptions(fixture.EndPoint, 1000001));
                await using var targetHotspot = new VirtualHotspot(CreateOptions(fixture.EndPoint, 1000002));
                await using var source = new VirtualRadio(sourceHotspot, new VirtualRadioOptions { RadioId = 10001 });
                await sourceHotspot.LoginAsync(timeout.Token);
                await targetHotspot.LoginAsync(timeout.Token);
                await source.AnnounceAsync(timeout.Token);

                var result = await source.CheckRadioAsync(10002, timeout.Token);
                await WaitUntilAsync(() => outbox.Count == 1, timeout.Token);

                Assert.Equal(new RadioControlSendResult(RadioControlSendStatus.TimedOut, 3), result);
                Assert.Equal(1, outbox.Count);

                await using var target = new VirtualRadio(targetHotspot, new VirtualRadioOptions { RadioId = 10002 });
                await target.AnnounceAsync(timeout.Token);
                var message = await target.ReceiveSdsAsync(timeout.Token);
                await WaitUntilAsync(() => outbox.Count == 0, timeout.Token);

                Assert.Equal(settings.RadioCheck.FallbackServiceId, message.SourceId);
                Assert.Equal(10002, message.DestinationId);
                Assert.Equal("Verbindungsversuch von 10001", message.Text);
            }
            finally
            {
                await coordinator.StopAsync(CancellationToken.None);
                await fallback.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task VirtualRadio_RetriesNackAndReportsRejection()
    {
        await using var fixture = await DmrLoopbackFixture.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var sourceHotspot = new VirtualHotspot(CreateOptions(fixture.EndPoint, 1000001) with
        {
            ReceiveQueueCapacity = 32
        });
        await using var targetHotspot = new VirtualHotspot(CreateOptions(fixture.EndPoint, 1000002) with
        {
            ReceiveQueueCapacity = 32
        });
        await using var source = new VirtualRadio(sourceHotspot, new VirtualRadioOptions
        {
            RadioId = 10001,
            MaximumAttempts = 2
        });
        await using var targetRoute = new VirtualRadio(targetHotspot, new VirtualRadioOptions { RadioId = 10002 });
        await sourceHotspot.LoginAsync(timeout.Token);
        await targetHotspot.LoginAsync(timeout.Token);
        await targetRoute.AnnounceAsync(timeout.Token);
        await targetRoute.DisposeAsync();
        var nackCount = 0;
        targetHotspot.DmrdReceived += RespondWithNack;

        var result = await source.SendSdsAsync(SdsTargetKind.Radio, 10002, "Bitte ablehnen", timeout.Token);
        await WaitUntilAsync(() => Volatile.Read(ref nackCount) == 2, timeout.Token);

        Assert.Equal(SdsSendStatus.Rejected, result.Status);
        Assert.Equal(2, result.Attempts);
        return;

        void RespondWithNack(HomebrewDatagram datagram)
        {
            if (!HomebrewProtocol.TryReadDmrdInfo(datagram.Payload, out var info) ||
                info.SourceId != 10001 || info.DestinationId != 10002 || info.DataType != 0x06) return;
            Span<byte> decoded = stackalloc byte[12];
            Bptc19696.Decode(datagram.Payload.AsSpan(20, 33), decoded);
            if ((decoded[0] & 0x0F) != 0x03) return;
            var sequence = (byte)((decoded[9] >> 4) & 0x07);
            var packet = new byte[DmrBurstWriter.DmrdLength];
            Span<byte> burst = stackalloc byte[DmrBurstWriter.BurstLength];
            DmrBurstWriter.WriteConfirmedResponseHeader(burst, 10002, 10001, sequence, false, 1);
            DmrBurstWriter.WriteDmrd(packet, burst, (byte)Interlocked.Increment(ref nackCount),
                10002, 10001, 1000002, 0x06, (uint)nackCount, 2, false);
            _ = targetHotspot.SendDmrdAsync(packet, timeout.Token);
        }
    }

    [Fact]
    public async Task PlaygroundRepl_LoginRunReceiveAndDisconnectOverLoopback()
    {
        await using var fixture = await DmrLoopbackFixture.StartAsync();
        var sourceFrame = CapturedGroupDataFrame.ToArray();
        sourceFrame[8] = 0;
        sourceFrame[9] = 0;
        sourceFrame[10] = 9;
        var scenarioPath = Path.Combine(Path.GetTempPath(), $"dmroute-playground-{Guid.NewGuid():N}.dmrd");
        await File.WriteAllTextAsync(scenarioPath,
            $"name=route test{Environment.NewLine}radioId=10001{Environment.NewLine}" +
            $"0 {Convert.ToHexString(sourceFrame)}{Environment.NewLine}");

        var settings = new PlaygroundSettings
        {
            Master = new PlaygroundMasterSettings
            {
                Host = fixture.EndPoint.Address.ToString(),
                Port = fixture.EndPoint.Port
            },
            Hotspots =
            [
                CreatePlaygroundHotspot("source", 1000001),
                CreatePlaygroundHotspot("target", 1000002)
            ]
        };
        await using var playground = new PlaygroundRepl(settings, fixture.EndPoint);
        var commands = string.Join(Environment.NewLine,
            "login all",
            "ping all",
            $"run source \"{scenarioPath}\"",
            "receive target 1000",
            "disconnect all",
            "exit") + Environment.NewLine;
        var output = new StringWriter();

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await playground.RunAsync(new StringReader(commands), output, timeout.Token);
        }
        finally
        {
            File.Delete(scenarioPath);
        }

        var text = output.ToString();
        Assert.Contains("source: angemeldet", text);
        Assert.Contains("target: Pong empfangen", text);
        Assert.Contains("Szenario abgeschlossen", text);
        Assert.Contains("src=10001 dst=9 repeater=1000001", text);
        Assert.Contains("target: abgemeldet", text);
        Assert.DoesNotContain("secret1000001", text);
    }

    [Fact]
    public async Task PlaygroundRepl_SendsAndDisplaysControlTransactions()
    {
        await using var fixture = await DmrLoopbackFixture.StartAsync();
        var settings = new PlaygroundSettings
        {
            Master = new PlaygroundMasterSettings
            {
                Host = fixture.EndPoint.Address.ToString(),
                Port = fixture.EndPoint.Port
            },
            Hotspots =
            [
                CreatePlaygroundHotspot("source", 1000001),
                CreatePlaygroundHotspot("target", 1000002)
            ],
            Radios =
            [
                new PlaygroundRadioSettings { Name = "r1", RadioId = 10001, Hotspot = "source" },
                new PlaygroundRadioSettings { Name = "r2", RadioId = 10002, Hotspot = "target" }
            ]
        };
        await using var playground = new PlaygroundRepl(settings, fixture.EndPoint);
        var output = new StringWriter();
        var commands = string.Join(Environment.NewLine,
            "login all",
            "radio announce r2",
            "radio alert r1 10002",
            "radio check r1 10002",
            "radio events r1",
            "radio events r2",
            "exit") + Environment.NewLine;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await playground.RunAsync(new StringReader(commands), output, timeout.Token);

        var text = output.ToString();
        Assert.Contains("r1: Call Alert nach 1 Versuch(en) bestätigt", text);
        Assert.Contains("r1: Radio Check nach 1 Versuch(en) bestätigt", text);
        Assert.Contains("Call Alert Bestätigung 10002 -> 10001", text);
        Assert.Contains("Radio Check Bestätigung 10002 -> 10001", text);
        Assert.Contains("Call Alert Anfrage 10001 -> 10002", text);
        Assert.Contains("Radio Check Anfrage 10001 -> 10002", text);
    }

    [Fact]
    public async Task PlaygroundRepl_ExplainsReservedPrivateTargetAndAcceptsServiceSyntax()
    {
        await using var fixture = await DmrLoopbackFixture.StartAsync();
        var settings = new PlaygroundSettings
        {
            Master = new PlaygroundMasterSettings(),
            Hotspots = [CreatePlaygroundHotspot("hs1", 1000001)],
            Radios = [new PlaygroundRadioSettings { Name = "radio1", RadioId = 10001, Hotspot = "hs1" }]
        };
        await using var repl = new PlaygroundRepl(settings, fixture.EndPoint);
        var output = new StringWriter();

        await repl.RunAsync(new StringReader(
            "login hs1\nsds send radio1 fug 990099 \"falsch\"\n" +
            "sds send radio1 service 990099 \"ECHO Test\"\nexit\n"), output);

        var text = output.ToString();
        Assert.Contains("990099 ist eine Service-ID", text);
        Assert.Contains("SDS unbestätigt gesendet", text);
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

    private static DmRouteSettings CreateRadioCheckSettings(string directory) =>
        ConfigurationTests.ValidSettings() with
        {
            ZoneId = 100,
            Sds = new SdsSettings { MaxSessions = 8, MaxMessageBytes = 512 },
            RadioCheck = new RadioCheckSettings
            {
                Enabled = true,
                MaxPending = 16,
                FallbackSdsEnabled = true,
                FallbackCapacity = 8,
                OutboxPath = Path.Combine(directory, "radio-check-outbox.json")
            }
        };

    private static byte[] BuildRadioCheckAcknowledgement()
    {
        var packet = new byte[DmrBurstWriter.DmrdLength];
        Span<byte> burst = stackalloc byte[DmrBurstWriter.BurstLength];
        DmrBurstWriter.WriteRadioCheckAcknowledgement(burst, 10101, 10001, 1);
        DmrBurstWriter.WriteDmrd(packet, burst, 1, 10101, 10001, 1000001, 0x03, 1);
        return packet;
    }

    private static PlaygroundHotspotSettings CreatePlaygroundHotspot(string name, int repeaterId) => new()
    {
        Name = name,
        RepeaterId = repeaterId,
        PreSharedKey = $"secret{repeaterId}",
        KeepaliveSeconds = 60,
        OperationTimeoutSeconds = 1,
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
        public MicroSubnetRouter Router => _router;
        public MasterRegistry Masters => _masters;
        public RoamingRegistry Roaming => _roaming;
        public MeshDiscoveryService Mesh => _mesh;
        public IDmrSender Sender => _server;
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
