using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DMRoute_ng.Core;
using DMRoute_ng.Gateways;
using DMRoute_ng.Integration;
using DMRoute_ng.Registry;
using DMRoute_ng.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace DMRoute_ng.Tests;

public sealed class LocationMqttTests
{
    private static readonly byte[][] CapturedFixedBeaconFrames =
    {
        Convert.FromHexString("444D5244050027110F1B93000F4241A6A3E1BE9413ED97E6073B088BA733CF8F058D5D7F77FD757338C1C05A79680661090210199A"),
        Convert.FromHexString("444D5244060027110F1B93000F4241A7A3E1BE9407C606020E4D21E815A06400C5ED5D7F77FD7570966C15502F5275A423016E1262"),
        Convert.FromHexString("444D5244070027110F1B93000F4241A7A3E1BE94454F74863027335932F6E059C5ED5D7F77FD7570950852F4F767374133CD281B03"),
        Convert.FromHexString("444D5244080027110F1B93000F4241A7A3E1BE9469928628777C23380231023485ED5D7F77FD7570963BF2BAB5313A242F912A2664"),
        Convert.FromHexString("444D5244090027110F1B93000F4241A7A3E1BE94684E8096745C236022A013D605ED5D7F77FD7570943F9CDAB39037043A95382C02"),
        Convert.FromHexString("444D52440A0027110F1B93000F4241A7A3E1BE94645BA88A02707BE01350A27145ED5D7F77FD75709427587BA01137053DD432AC4C"),
        Convert.FromHexString("444D52440B0027110F1B93000F4241A7A3E1BE947476BA4A271C337803C2A35405ED5D7F77FD75709513F042F230390638D03AA147"),
        Convert.FromHexString("444D52440C0027110F1B93000F4241A7A3E1BE946F008228724C384C202D816D45ED5D7F77FD7570964EC8DA88734040025204020D"),
        Convert.FromHexString("444D52440D0027110F1B93000F4241A7A3E1BE9400D2009801FC01900A301A4005ED5D7F77FD757097A000780E5018600D0056805C")
    };

    [Theory]
    [InlineData(null, true)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public async Task MqttLocation_UsesSafeDefaultsAndConfiguredPrivacy(
        string? privacySetting, bool privacyEnabled)
    {
        using var fixture = new MqttLocationFixture();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var broker = ReceiveLocationMessages(listener, timeout.Token);
        var settings = new Dictionary<string, string?>
        {
            ["ZoneId"] = "100",
            ["Mqtt:Host"] = "127.0.0.1",
            ["Mqtt:Port"] = port.ToString(CultureInfo.InvariantCulture),
            ["Mqtt:StateIntervalSeconds"] = "60"
        };
        if (privacySetting is not null)
        {
            settings["Mqtt:LocationPrivacyEnabled"] = privacySetting;
            settings["Mqtt:LocationGridKm"] = "10";
        }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var gateway = new SdsGateway(NullLogger<SdsGateway>.Instance, fixture.Router, 4, 512);
        using var client = new RawMqttClient("location-integration"u8.ToArray());
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

        foreach (var frame in CapturedFixedBeaconFrames) gateway.HandleDataFrame(frame);
        var gpsText = Encoding.Unicode.GetBytes(
            "Template:\r\n52.520000N\r\ninvalid longitude 013.405000E\r\n");
        Assert.False(LocationDecoder.TryDecodeAnytoneGpsText(0x04, gpsText, out _));
        gateway.PublishSms(10001, 10101, 0x04, gpsText,
            LocationDecoder.LooksLikeLocationText(0x04, gpsText));

        var messages = await broker;
        await service.StopAsync(timeout.Token);

        var gps = Assert.Single(messages, static message => message.Topic == "dmroute/100/sds/gps");
        var sms = Assert.Single(messages, static message => message.Topic == "dmroute/100/sds/sms");
        Assert.False(gps.Retain);
        Assert.False(sms.Retain);
        using var gpsJson = JsonDocument.Parse(gps.Payload);
        var root = gpsJson.RootElement;
        Assert.Equal(10001, root.GetProperty("srcId").GetInt32());
        Assert.Equal(990099, root.GetProperty("dstId").GetInt32());
        Assert.Equal(1000001, root.GetProperty("hotspotId").GetInt32());
        Assert.Equal("NMEA_RMC", root.GetProperty("format").GetString());
        Assert.True(root.GetProperty("fixValid").GetBoolean());
        Assert.Equal(privacyEnabled, root.GetProperty("obfuscated").GetBoolean());

        var latitude = root.GetProperty("lat").GetDouble();
        var longitude = root.GetProperty("lon").GetDouble();
        if (privacyEnabled)
        {
            Assert.Equal(10d, root.GetProperty("precisionKm").GetDouble());
            Assert.InRange(DistanceKilometers(34.2d, 108.83333333333333d, latitude, longitude), 0d, 10d);
            Assert.NotEqual(34.2d, latitude);
            Assert.NotEqual(108.83333333333333d, longitude);
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, root.GetProperty("precisionKm").ValueKind);
            Assert.Equal(34.2d, latitude, 6);
            Assert.Equal(108.83333333333333d, longitude, 6);
        }

        using var smsJson = JsonDocument.Parse(sms.Payload);
        var mqttSms = smsJson.RootElement.GetProperty("message").GetString();
        var expectedSms = Encoding.Unicode.GetString(gpsText).Trim('\0', '\r', '\n');
        Assert.Equal(privacyEnabled ? "[GPS position redacted]" : expectedSms, mqttSms);

        if (privacyEnabled)
        {
            foreach (var message in messages)
            {
                var payload = Encoding.UTF8.GetString(message.Payload);
                Assert.DoesNotContain("52.520000", payload, StringComparison.Ordinal);
                Assert.DoesNotContain("013.405000", payload, StringComparison.Ordinal);
                Assert.DoesNotContain("34.200000", payload, StringComparison.Ordinal);
                Assert.DoesNotContain("108.833333", payload, StringComparison.Ordinal);
            }
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1000.1")]
    [InlineData("NaN")]
    public void MqttLocation_RejectsInvalidGridSize(string gridSize)
    {
        using var fixture = new MqttLocationFixture();
        var settings = new Dictionary<string, string?>
        {
            ["Mqtt:Host"] = "127.0.0.1",
            ["Mqtt:LocationGridKm"] = gridSize
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        using var client = new RawMqttClient("invalid-grid"u8.ToArray());

        Assert.Throws<ArgumentOutOfRangeException>(() => new MqttIntegrationService(
            NullLogger<MqttIntegrationService>.Instance,
            fixture.Router,
            new SdsGateway(NullLogger<SdsGateway>.Instance, fixture.Router, 4, 512),
            fixture.Repeaters,
            fixture.Masters,
            fixture.Roaming,
            configuration,
            client));
    }

    private static async Task<List<MqttPublish>> ReceiveLocationMessages(TcpListener listener, CancellationToken token)
    {
        using var socket = await listener.AcceptSocketAsync(token);
        var stream = new NetworkStream(socket, ownsSocket: false);
        var connect = await ReadMqttPacket(stream, token);
        Assert.Equal(0x10, connect.Header);
        await stream.WriteAsync(new byte[] { 0x20, 0x02, 0x00, 0x00 }, token);

        var messages = new List<MqttPublish>();
        while (!messages.Any(static message => message.Topic == "dmroute/100/sds/gps") ||
               !messages.Any(static message => message.Topic == "dmroute/100/sds/sms"))
        {
            var packet = await ReadMqttPacket(stream, token);
            if ((packet.Header & 0xF0) != 0x30) continue;
            var topicLength = BinaryPrimitives.ReadUInt16BigEndian(packet.Body);
            var topic = Encoding.UTF8.GetString(packet.Body.AsSpan(2, topicLength));
            messages.Add(new MqttPublish(topic, (packet.Header & 1) != 0, packet.Body[(2 + topicLength)..]));
        }
        return messages;
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

    private static double DistanceKilometers(double latitude1, double longitude1, double latitude2, double longitude2)
    {
        const double earthRadiusKilometers = 6371d;
        var lat1 = latitude1 * Math.PI / 180d;
        var lat2 = latitude2 * Math.PI / 180d;
        var deltaLatitude = (latitude2 - latitude1) * Math.PI / 180d;
        var deltaLongitude = (longitude2 - longitude1) * Math.PI / 180d;
        var a = Math.Sin(deltaLatitude / 2d) * Math.Sin(deltaLatitude / 2d) +
                Math.Cos(lat1) * Math.Cos(lat2) *
                Math.Sin(deltaLongitude / 2d) * Math.Sin(deltaLongitude / 2d);
        return earthRadiusKilometers * 2d * Math.Asin(Math.Sqrt(a));
    }

    private readonly record struct MqttPacket(byte Header, byte[] Body);
    private readonly record struct MqttPublish(string Topic, bool Retain, byte[] Payload);

    private sealed class MqttLocationFixture : IDisposable
    {
        private readonly MeshDiscoveryService _mesh;
        public RepeaterRegistry Repeaters { get; }
        public MasterRegistry Masters { get; }
        public RoamingRegistry Roaming { get; }
        public MicroSubnetRouter Router { get; }

        public MqttLocationFixture()
        {
            Repeaters = new RepeaterRegistry(NullLogger<RepeaterRegistry>.Instance, 100, "secret");
            Masters = new MasterRegistry(NullLogger<MasterRegistry>.Instance);
            Roaming = new RoamingRegistry(NullLogger<RoamingRegistry>.Instance, 8);
            _mesh = new MeshDiscoveryService(NullLogger<MeshDiscoveryService>.Instance, Masters, Roaming,
                100, 62031, 0, "mesh");
            Router = new MicroSubnetRouter(NullLogger<MicroSubnetRouter>.Instance, Repeaters, Masters, Roaming,
                _mesh, 100, 8, 8);
        }

        public void Dispose()
        {
            Router.Dispose();
            _mesh.Dispose();
            Repeaters.Dispose();
        }
    }
}
