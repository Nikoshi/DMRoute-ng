using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DMRoute_ng.Core;
using DMRoute_ng.Gateways;
using DMRoute_ng.Integration;
using DMRoute_ng.Registry;
using DMRoute_ng.Routing;
using DMRoute_ng.Types;
using DMRoute_ng.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;

namespace DMRoute_ng.Tests;

public sealed class HotPathTests
{
    [Fact]
    public void PacketWriters_ProduceExpectedWireFormat()
    {
        Span<byte> packet = stackalloc byte[PacketUtils.MstPongLength];
        Assert.True(PacketUtils.TryWriteMstPong(packet, 1000001, out var written));
        Assert.Equal(PacketUtils.MstPongLength, written);
        Assert.True(packet[..7].SequenceEqual("MSTPONG"u8));
        Assert.Equal(1000001, BinaryPrimitives.ReadInt32BigEndian(packet[7..]));

        Assert.False(PacketUtils.TryWriteMstPong(packet[..10], 1000001, out written));
        Assert.Equal(0, written);
    }

    [Fact]
    public void Ipv4Endpoint_RoundTripsThroughSocketAddress()
    {
        var expected = Ipv4Endpoint.FromIPEndPoint(new IPEndPoint(IPAddress.Parse("192.0.2.42"), 62031));
        var address = new SocketAddress(AddressFamily.InterNetwork, 16);
        expected.WriteTo(address);
        Assert.Equal(expected, Ipv4Endpoint.FromSocketAddress(address));
    }

    [Fact]
    public void Ipv4Endpoint_FormatsWithoutCreatingAnEndpointString()
    {
        var endpoint = new Ipv4Endpoint(0xC000022A, 62031);
        Span<byte> buffer = stackalloc byte[32];

        Assert.True(endpoint.TryFormatUtf8(buffer, out var written));
        Assert.True(buffer[..written].SequenceEqual("192.0.2.42:62031"u8));
        Assert.False(endpoint.TryFormatUtf8(buffer[..4], out written));
        Assert.Equal(0, written);
    }

    [Fact]
    public void Ipv4Endpoint_SocketAddressCanSendDatagram()
    {
        using var receiver = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        using var sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        receiver.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        receiver.ReceiveTimeout = 2000;
        var target = Ipv4Endpoint.FromIPEndPoint((IPEndPoint)receiver.LocalEndPoint!);
        var address = new SocketAddress(AddressFamily.InterNetwork, 16);
        target.WriteTo(address);

        sender.SendTo("DMRD"u8, SocketFlags.None, address);
        Span<byte> received = stackalloc byte[4];
        var count = receiver.Receive(received);

        Assert.Equal(4, count);
        Assert.True(received.SequenceEqual("DMRD"u8));
    }

    [Fact]
    public void JsonSpanBuilder_EscapesStringValues()
    {
        Span<byte> buffer = stackalloc byte[128];
        var builder = new JsonSpanBuilder(buffer);
        builder.AppendString("text"u8, "a\"b\\c\n");
        builder.Finish();
        Assert.True(buffer[..builder.Length].SequenceEqual("{\"text\":\"a\\\"b\\\\c\\n\"}"u8));
    }

    [Fact]
    public void JsonSpanBuilder_WritesPreciseDurationTimestampAndHex()
    {
        Span<byte> buffer = stackalloc byte[192];
        var builder = new JsonSpanBuilder(buffer);
        builder.AppendDecimal("durationSec"u8, 4.56789);
        builder.AppendTimestamp("timestamp"u8,
            new DateTime(2026, 9, 12, 12, 34, 56, DateTimeKind.Utc).Ticks);
        builder.AppendHexString("hexDump"u8, new byte[] { 0x00, 0xAF, 0x10 });
        builder.Finish();

        Assert.True(buffer[..builder.Length].SequenceEqual(
            "{\"durationSec\":4.568,\"timestamp\":\"2026-09-12T12:34:56.0000000Z\",\"hexDump\":\"00AF10\"}"u8));
    }

    [Fact]
    public void MqttTelemetryQueue_CopiesBorrowedDiagnosticFrameAndDropsOldest()
    {
        var queue = new MqttTelemetryQueue(1, 1, 16);
        Span<byte> first = stackalloc byte[] { 0x01, 0x02, 0x03 };
        queue.EnqueueDiagnostic(first, 10001, 1000001, 0x0A, 123);
        first.Clear();
        Span<byte> second = stackalloc byte[] { 0xAA, 0xBB };
        queue.EnqueueDiagnostic(second, 10101, 1000042, 0x0B, 456);

        Assert.Equal(1, queue.DroppedEvents);
        Assert.True(queue.DiagnosticReader.TryRead(out var item));
        Assert.Equal(10101, item.SourceId);
        Assert.Equal(1000042, item.HotspotId);
        Assert.Equal(0x0B, item.DataType);
        Assert.True(item.DiagnosticFrame!.Buffer.AsSpan(0, item.DiagnosticFrame.Length)
            .SequenceEqual(new byte[] { 0xAA, 0xBB }));
        MqttTelemetryQueue.Release(item);
    }

    [Fact]
    public void WarmedMqttTelemetryEnqueue_AllocatesNoManagedBytes()
    {
        var queue = new MqttTelemetryQueue(16, 2, 64);
        Span<byte> packet = stackalloc byte[53];
        packet.Fill(0x5A);
        for (var i = 0; i < 32; i++)
        {
            queue.EnqueueDiagnostic(packet, 10001, 1000001, 0x0A, 123);
            Assert.True(queue.DiagnosticReader.TryRead(out var warmup));
            MqttTelemetryQueue.Release(warmup);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            queue.EnqueueDiagnostic(packet, 10001, 1000001, 0x0A, 123);
            Assert.True(queue.DiagnosticReader.TryRead(out var item));
            MqttTelemetryQueue.Release(item);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void RouterMqttHooks_PreservePacketHotspotIdWithoutAllocating()
    {
        using var fixture = new RouterFixture();
        var queue = new MqttTelemetryQueue(8, 2, 64);
        var sink = new MqttEventSink(queue);
        fixture.Router.OnCallEvent += sink.HandleCall;
        fixture.Router.OnUnknownFrameReceived += sink.HandleUnknown;
        fixture.Repeaters.TryGet(1000042, out var source);
        source.EndPoint = fixture.SourceEndpoint;
        source.State = RepeaterState.LoggedIn;
        var sender = new CountingSender();

        Span<byte> packet = stackalloc byte[53];
        BuildDmrd(packet, 10001, 9, 1000042, 0x01);
        fixture.Router.RouteDmrd(packet, fixture.SourceEndpoint, sender);
        Assert.True(queue.EventReader.TryRead(out _));
        BuildDmrd(packet, 10001, 9, 1000042, 0x0A);
        fixture.Router.RouteDmrd(packet, fixture.SourceEndpoint, sender);
        Assert.True(queue.DiagnosticReader.TryRead(out var warmupDiagnostic));
        MqttTelemetryQueue.Release(warmupDiagnostic);

        BuildDmrd(packet, 10002, 9, 1000042, 0x01);
        var before = GC.GetAllocatedBytesForCurrentThread();
        fixture.Router.RouteDmrd(packet, fixture.SourceEndpoint, sender);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.True(queue.EventReader.TryRead(out var call));
        Assert.Equal(MqttTelemetryEventType.Call, call.EventType);
        Assert.Equal(1000042, call.Call.HotspotId);

        BuildDmrd(packet, 10003, 9, 1000042, 0x0A);
        before = GC.GetAllocatedBytesForCurrentThread();
        fixture.Router.RouteDmrd(packet, fixture.SourceEndpoint, sender);
        allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.True(queue.DiagnosticReader.TryRead(out var diagnostic));
        Assert.Equal(1000042, diagnostic.HotspotId);
        MqttTelemetryQueue.Release(diagnostic);
    }

    [Fact]
    public void WarmedGroupRouting_AllocatesNoManagedBytes()
    {
        using var fixture = new RouterFixture();
        var sender = new CountingSender();
        Span<byte> packet = stackalloc byte[53];
        BuildDmrd(packet, 10001, 1, 1000001, 0x04);

        for (var i = 0; i < 100; i++) fixture.Router.RouteDmrd(packet, fixture.SourceEndpoint, sender);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) fixture.Router.RouteDmrd(packet, fixture.SourceEndpoint, sender);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(1100, sender.SendCount);
    }

    [Fact]
    public void WarmedSdsHeaderIngestion_AllocatesNoManagedBytes()
    {
        using var fixture = new RouterFixture();
        var gateway = new SdsGateway(NullLogger<SdsGateway>.Instance, fixture.Router, 4, 256);
        Span<byte> packet = stackalloc byte[53];
        BuildDmrd(packet, 10001, 10002, 1000001, 0x26);

        for (var i = 0; i < 100; i++) gateway.HandleDataFrame(packet);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) gateway.HandleDataFrame(packet);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void WarmedPingHandling_AllocatesNoManagedBytes()
    {
        using var fixture = new RouterFixture();
        using var server = new DmrServer(NullLogger<DmrServer>.Instance, fixture.Repeaters, fixture.Router);
        Span<byte> packet = stackalloc byte[11];
        "RPTPING"u8.CopyTo(packet);
        BinaryPrimitives.WriteInt32BigEndian(packet[7..], 1000001);

        for (var i = 0; i < 100; i++) server.HandlePacket(packet, fixture.SourceEndpoint);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) server.HandlePacket(packet, fixture.SourceEndpoint);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void UnknownDmrdAndPing_DoNotAllocateOrEnrollRepeater()
    {
        using var fixture = new RouterFixture();
        using var server = new DmrServer(NullLogger<DmrServer>.Instance, fixture.Repeaters, fixture.Router);
        Span<byte> dmrd = stackalloc byte[53];
        BuildDmrd(dmrd, 10001, 1, 1000999, 0x04);
        Span<byte> ping = stackalloc byte[11];
        "RPTPING"u8.CopyTo(ping);
        BinaryPrimitives.WriteInt32BigEndian(ping[7..], 1000999);

        for (var i = 0; i < 100; i++)
        {
            server.HandlePacket(dmrd, fixture.SourceEndpoint);
            server.HandlePacket(ping, fixture.SourceEndpoint);
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            server.HandlePacket(dmrd, fixture.SourceEndpoint);
            server.HandlePacket(ping, fixture.SourceEndpoint);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.False(fixture.Repeaters.TryGetExisting(1000999, out _));
    }

    [Fact]
    public void FullCallTable_ContinuesRoutingWithoutAllocating()
    {
        using var fixture = new RouterFixture(maxActiveCalls: 2, maxLocalRoutes: 2);
        var sender = new CountingSender();
        Span<byte> packet = stackalloc byte[53];
        for (var sourceId = 10000; sourceId < 10002; sourceId++)
        {
            BuildDmrd(packet, sourceId, 1, 1000001, 0x01);
            fixture.Router.RouteDmrd(packet, fixture.SourceEndpoint, sender);
        }

        BuildDmrd(packet, 10002, 1, 1000001, 0x01);
        fixture.Router.RouteDmrd(packet, fixture.SourceEndpoint, sender);
        var droppedBefore = fixture.Router.DroppedCallStates;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) fixture.Router.RouteDmrd(packet, fixture.SourceEndpoint, sender);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(droppedBefore + 1000, fixture.Router.DroppedCallStates);
        Assert.Equal(1003, sender.SendCount);
    }

    [Fact]
    public void NewCallWithinReservedCapacity_AllocatesNoManagedBytes()
    {
        using var fixture = new RouterFixture();
        var sender = new CountingSender();
        Span<byte> packet = stackalloc byte[53];
        BuildDmrd(packet, 10001, 1, 1000001, 0x01);
        fixture.Router.RouteDmrd(packet, fixture.SourceEndpoint, sender);

        BuildDmrd(packet, 10002, 1, 1000001, 0x01);
        var before = GC.GetAllocatedBytesForCurrentThread();
        fixture.Router.RouteDmrd(packet, fixture.SourceEndpoint, sender);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void NewSdsSessionWithinReservedCapacity_AllocatesNoManagedBytes()
    {
        using var fixture = new RouterFixture();
        var gateway = new SdsGateway(NullLogger<SdsGateway>.Instance, fixture.Router, 4, 256);
        Span<byte> packet = stackalloc byte[53];
        BuildDmrd(packet, 10001, 10002, 1000001, 0x26);
        gateway.HandleDataFrame(packet);

        BuildDmrd(packet, 10002, 10003, 1000001, 0x26);
        var before = GC.GetAllocatedBytesForCurrentThread();
        gateway.HandleDataFrame(packet);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public async Task RawMqttClient_ReadsFragmentedConnAckAndPublishesFrame()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var broker = Task.Run(async () =>
        {
            using var socket = await listener.AcceptSocketAsync(timeout.Token);
            var connect = new byte[128];
            Assert.True(await socket.ReceiveAsync(connect, SocketFlags.None, timeout.Token) > 0);
            foreach (var value in new byte[] { 0x20, 0x02, 0x00, 0x00 })
            {
                await socket.SendAsync(new[] { value }, SocketFlags.None, timeout.Token);
                await Task.Delay(10, timeout.Token);
            }

            var publish = new byte[32];
            var received = await socket.ReceiveAsync(publish, SocketFlags.None, timeout.Token);
            return publish[..received];
        }, timeout.Token);

        using var client = new RawMqttClient("test-client"u8.ToArray());
        Assert.True(await client.ConnectAsync("127.0.0.1", port));
        client.Publish("abc"u8, "hi"u8);
        var frame = await broker;

        Assert.Equal(new byte[] { 0x30, 0x07, 0x00, 0x03, (byte)'a', (byte)'b', (byte)'c', (byte)'h', (byte)'i' }, frame);
    }

    [Fact]
    public async Task MqttIntegration_PublishesCanonicalCallTopicsWithPacketHotspotId()
    {
        using var fixture = new RouterFixture();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var startSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var broker = Task.Run(async () =>
        {
            using var socket = await listener.AcceptSocketAsync(timeout.Token);
            var stream = new NetworkStream(socket, ownsSocket: false);
            var connect = await ReadMqttPacket(stream, timeout.Token);
            Assert.Equal(0x10, connect.Header);
            await stream.WriteAsync(new byte[] { 0x20, 0x02, 0x00, 0x00 }, timeout.Token);

            var messages = new List<MqttPublish>();
            while (!messages.Any(static message => message.Topic == "dmroute/100/call/event"))
            {
                var packet = await ReadMqttPacket(stream, timeout.Token);
                if ((packet.Header & 0xF0) != 0x30) continue;
                var topicLength = BinaryPrimitives.ReadUInt16BigEndian(packet.Body);
                var topic = System.Text.Encoding.UTF8.GetString(packet.Body.AsSpan(2, topicLength));
                messages.Add(new MqttPublish(topic, (packet.Header & 1) != 0, packet.Body[(2 + topicLength)..]));
                if (messages.Any(static message => message.Topic == "dmroute/100/call/active") &&
                    messages.Any(static message =>
                        message.Topic == "dmroute/100/call/active/10001" && message.Payload.Length > 0))
                    startSeen.TrySetResult();
            }
            return messages;
        }, timeout.Token);

        var settings = new Dictionary<string, string?>
        {
            ["ZoneId"] = "100",
            ["Mqtt:Host"] = "127.0.0.1",
            ["Mqtt:Port"] = port.ToString(),
            ["Mqtt:StateIntervalSeconds"] = "60"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var gateway = new SdsGateway(NullLogger<SdsGateway>.Instance, fixture.Router, 4, 256);
        using var client = new RawMqttClient("integration-test"u8.ToArray());
        using var service = new MqttIntegrationService(
            NullLogger<MqttIntegrationService>.Instance,
            fixture.Router,
            gateway,
            fixture.Repeaters,
            fixture.Masters,
            fixture.Roaming,
            configuration,
            client);
        await service.StartAsync(timeout.Token);

        var dmrd = new byte[53];
        BuildDmrd(dmrd, 10001, 9, 1000001, 0x01);
        fixture.Router.RouteDmrd(dmrd, fixture.SourceEndpoint, new CountingSender());
        await startSeen.Task.WaitAsync(timeout.Token);
        BuildDmrd(dmrd, 10001, 9, 1000001, 0x02);
        fixture.Router.RouteDmrd(dmrd, fixture.SourceEndpoint, new CountingSender());
        var publishes = await broker;
        await service.StopAsync(timeout.Token);

        var streamEvent = Assert.Single(publishes, static message => message.Topic == "dmroute/100/call/active");
        var retainedState = publishes.First(static message =>
            message.Topic == "dmroute/100/call/active/10001" && message.Payload.Length > 0);
        var tombstone = Assert.Single(publishes, static message =>
            message.Topic == "dmroute/100/call/active/10001" && message.Payload.Length == 0);
        var endEvent = Assert.Single(publishes, static message => message.Topic == "dmroute/100/call/event");
        Assert.False(streamEvent.Retain);
        Assert.True(retainedState.Retain);
        Assert.True(tombstone.Retain);
        Assert.False(endEvent.Retain);
        using var payload = JsonDocument.Parse(retainedState.Payload);
        Assert.Equal(1000001, payload.RootElement.GetProperty("hotspotId").GetInt32());
        Assert.Equal(9, payload.RootElement.GetProperty("dstId").GetInt32());
        using var endPayload = JsonDocument.Parse(endEvent.Payload);
        Assert.Equal("Terminated", endPayload.RootElement.GetProperty("endReason").GetString());
        Assert.InRange(endPayload.RootElement.GetProperty("durationSec").GetDouble(), 0, 1);
    }

    [Fact]
    public async Task DmrServer_StartAndStop_CompletesPromptly()
    {
        using var fixture = new RouterFixture();
        using var server = new DmrServer(NullLogger<DmrServer>.Instance, fixture.Repeaters, fixture.Router);
        await server.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await server.StopAsync(timeout.Token);
    }

    [Fact]
    public async Task MeshDiscovery_StartAndStop_CompletesPromptly()
    {
        using var fixture = new RouterFixture();
        await fixture.Mesh.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await fixture.Mesh.StopAsync(timeout.Token);
    }

    private static void BuildDmrd(Span<byte> packet, int sourceId, int destinationId, int repeaterId, byte flags)
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

    private static async Task<MqttPacket> ReadMqttPacket(NetworkStream stream, CancellationToken token)
    {
        var single = new byte[1];
        await stream.ReadExactlyAsync(single, token);
        var header = single[0];
        var remaining = 0;
        var multiplier = 1;
        do
        {
            await stream.ReadExactlyAsync(single, token);
            remaining += (single[0] & 0x7F) * multiplier;
            multiplier *= 128;
        } while ((single[0] & 0x80) != 0);

        var body = new byte[remaining];
        await stream.ReadExactlyAsync(body, token);
        return new MqttPacket(header, body);
    }

    private readonly record struct MqttPacket(byte Header, byte[] Body);
    private readonly record struct MqttPublish(string Topic, bool Retain, byte[] Payload);

    private sealed class CountingSender : IDmrSender
    {
        public int SendCount { get; private set; }
        public void SendTo(ReadOnlySpan<byte> packet, Ipv4Endpoint endPoint) => SendCount++;
    }

    private sealed class MqttEventSink(MqttTelemetryQueue queue)
    {
        public void HandleCall(in DmrCallEvent call) => queue.EnqueueCall(call);

        public void HandleUnknown(
            ReadOnlySpan<byte> packet, int sourceId, int hotspotId, byte dataType, long occurredAtTicks) =>
            queue.EnqueueDiagnostic(packet, sourceId, hotspotId, dataType, occurredAtTicks);
    }

    private sealed class RouterFixture : IDisposable
    {
        public RepeaterRegistry Repeaters { get; }
        public MasterRegistry Masters { get; }
        public RoamingRegistry Roaming { get; }
        public MeshDiscoveryService Mesh { get; }
        public MicroSubnetRouter Router { get; }
        public Ipv4Endpoint SourceEndpoint { get; } = new(0x7F000001, 62031);

        public RouterFixture(int maxActiveCalls = 8, int maxLocalRoutes = 8)
        {
            Repeaters = new RepeaterRegistry(NullLogger<RepeaterRegistry>.Instance, 100, "secret");
            Masters = new MasterRegistry(NullLogger<MasterRegistry>.Instance);
            Roaming = new RoamingRegistry(NullLogger<RoamingRegistry>.Instance, 8);
            Mesh = new MeshDiscoveryService(NullLogger<MeshDiscoveryService>.Instance, Masters, Roaming, 100, 62031, 0, "mesh");
            Router = new MicroSubnetRouter(NullLogger<MicroSubnetRouter>.Instance, Repeaters, Masters, Roaming, Mesh,
                100, maxActiveCalls, maxLocalRoutes);

            Repeaters.TryGet(1000001, out var source);
            source.EndPoint = SourceEndpoint;
            source.State = RepeaterState.LoggedIn;
            Repeaters.TryGet(1000002, out var target);
            target.EndPoint = new Ipv4Endpoint(0x7F000001, 62032);
            target.State = RepeaterState.LoggedIn;
            Repeaters.RefreshRoutingSnapshot();
        }

        public void Dispose()
        {
            Router.Dispose();
            Mesh.Dispose();
            Repeaters.Dispose();
        }
    }
}
