using System.Text;
using DMRoute_ng.Core;
using DMRoute_ng.Gateways;
using DMRoute_ng.Integration;
using DMRoute_ng.Registry;
using DMRoute_ng.Routing;
using DMRoute_ng.Types;
using Microsoft.Extensions.Logging.Abstractions;

namespace DMRoute_ng.Tests;

public sealed class LocationTests
{
    private static readonly string[] CapturedFixedBeaconFrames =
    {
        "444D5244050027110F1B93000F4241A6A3E1BE9413ED97E6073B088BA733CF8F058D5D7F77FD757338C1C05A79680661090210199A",
        "444D5244060027110F1B93000F4241A7A3E1BE9407C606020E4D21E815A06400C5ED5D7F77FD7570966C15502F5275A423016E1262",
        "444D5244070027110F1B93000F4241A7A3E1BE94454F74863027335932F6E059C5ED5D7F77FD7570950852F4F767374133CD281B03",
        "444D5244080027110F1B93000F4241A7A3E1BE9469928628777C23380231023485ED5D7F77FD7570963BF2BAB5313A242F912A2664",
        "444D5244090027110F1B93000F4241A7A3E1BE94684E8096745C236022A013D605ED5D7F77FD7570943F9CDAB39037043A95382C02",
        "444D52440A0027110F1B93000F4241A7A3E1BE94645BA88A02707BE01350A27145ED5D7F77FD75709427587BA01137053DD432AC4C",
        "444D52440B0027110F1B93000F4241A7A3E1BE947476BA4A271C337803C2A35405ED5D7F77FD75709513F042F230390638D03AA147",
        "444D52440C0027110F1B93000F4241A7A3E1BE946F008228724C384C202D816D45ED5D7F77FD7570964EC8DA88734040025204020D",
        "444D52440D0027110F1B93000F4241A7A3E1BE9400D2009801FC01900A301A4005ED5D7F77FD757097A000780E5018600D0056805C"
    };

    [Fact]
    public void NmeaRmcDecoder_ParsesRepresentativeGpsBeacon()
    {
        ReadOnlySpan<byte> data = "\0\0$GPRMC,180206.000,A,5231.20000,N,01324.30000,E,0.00,133.23,120926,,,A*6D\r\n"u8;

        Assert.True(LocationDecoder.TryDecodeNmeaRmc(data, out var location));
        Assert.True(location.FixValid);
        Assert.Equal(DmrLocationFormat.NmeaRmc, location.Format);
        Assert.Equal(52.52d, location.Latitude!.Value, 10);
        Assert.Equal(13.405d, location.Longitude!.Value, 10);
        Assert.Equal(0d, location.SpeedMetersPerSecond!.Value);
        Assert.Equal(133.23d, location.CourseDegrees!.Value, 10);
        Assert.Null(location.AltitudeMeters);
    }

    [Fact]
    public void NmeaRmcDecoder_HandlesGnTalkerSouthernWesternAndInvalidFix()
    {
        Assert.True(LocationDecoder.TryDecodeNmeaRmc(
            "$GNRMC,180206.000,A,3456.0000,S,12345.0000,W,10.00,270.00,120926,,,A*4D"u8,
            out var southern));
        Assert.Equal(-34.93333333333333d, southern.Latitude!.Value, 10);
        Assert.Equal(-123.75d, southern.Longitude!.Value, 10);
        Assert.Equal(5.144444444444445d, southern.SpeedMetersPerSecond!.Value, 10);

        Assert.True(LocationDecoder.TryDecodeNmeaRmc(
            "$GPRMC,180206.000,V,,,,,0.00,,120926,,,N*50"u8, out var invalid));
        Assert.False(invalid.FixValid);
        Assert.Null(invalid.Latitude);
        Assert.Null(invalid.Longitude);
        Assert.Equal(0d, invalid.SpeedMetersPerSecond!.Value);
        Assert.Null(invalid.CourseDegrees);
    }

    [Theory]
    [InlineData("$GPRMC,180206.000,A,5231.20000,N,01324.30000,E,0.00,133.23,120926,,,A*00")]
    [InlineData("$GPRMC,180206.000,A,9160.0000,N,01324.30000,E,0.00,133.23,120926,,,A*54")]
    [InlineData("$GPRMC,180206.000,X,5231.20000,N,01324.30000,E,0.00,133.23,120926,,,A*74")]
    [InlineData("truncated $GPRMC,180206.000,A")]
    public void NmeaRmcDecoder_RejectsMalformedSentences(string text)
    {
        Assert.False(LocationDecoder.TryDecodeNmeaRmc(Encoding.ASCII.GetBytes(text), out _));
    }

    [Fact]
    public void AnytoneDecoder_ParsesCapturedGpsText()
    {
        var text = Encoding.Unicode.GetBytes(
            "Template:\r\n52.520000N\r\n013.405000E\r\n2026-09-12\r\n19:52:03\r\nV:0.0M/S\r\nH:34.5M\r\n");

        Assert.True(LocationDecoder.LooksLikeLocationText(0x04, text));
        Assert.True(LocationDecoder.TryDecodeAnytoneGpsText(0x04, text, out var location));
        Assert.Equal(DmrLocationFormat.AnytoneGpsText, location.Format);
        Assert.Equal(52.52d, location.Latitude!.Value, 10);
        Assert.Equal(13.405d, location.Longitude!.Value, 10);
        Assert.Equal(0d, location.SpeedMetersPerSecond!.Value);
        Assert.Null(location.CourseDegrees);
        Assert.Equal(34.5d, location.AltitudeMeters!.Value, 10);
    }

    [Fact]
    public void AnytoneLocationPrefix_IsRedactableEvenWhenPayloadIsMalformed()
    {
        var malformed = Encoding.Unicode.GetBytes("Template:\r\nnot-a-coordinate\r\n");
        var signed = Encoding.Unicode.GetBytes("Template:\r\n-12.3N\r\n045.6E\r\n");

        Assert.True(LocationDecoder.LooksLikeLocationText(0x04, malformed));
        Assert.False(LocationDecoder.TryDecodeAnytoneGpsText(0x04, malformed, out _));
        Assert.True(LocationDecoder.LooksLikeLocationText(0x04, signed));
        Assert.False(LocationDecoder.TryDecodeAnytoneGpsText(0x04, signed, out _));
    }

    [Fact]
    public void LocationPrivacy_UsesStableGridWithinTenKilometers()
    {
        var points = new (double Latitude, double Longitude)[]
        {
            (52.52d, 13.405d),
            (0.1d, 179.99d),
            (-33.8688d, 151.2093d),
            (89.99d, -179.99d)
        };

        foreach (var point in points)
        {
            LocationPrivacy.SnapToGrid(point.Latitude, point.Longitude, 10d, out var latitude, out var longitude);
            LocationPrivacy.SnapToGrid(point.Latitude, point.Longitude, 10d, out var repeatedLatitude,
                out var repeatedLongitude);
            Assert.Equal(latitude, repeatedLatitude);
            Assert.Equal(longitude, repeatedLongitude);
            Assert.InRange(latitude, -90d, 90d);
            Assert.InRange(longitude, -180d, 180d);
            Assert.InRange(DistanceKilometers(point.Latitude, point.Longitude, latitude, longitude), 0d, 10d);
        }
    }

    [Fact]
    public void CapturedDmrdFrames_ReassembleWithoutManagedAllocations()
    {
        using var fixture = new LocationRouterFixture();
        var gateway = new SdsGateway(NullLogger<SdsGateway>.Instance, fixture.Router, 4, 512);
        var sink = new LocationSink();
        gateway.OnLocationReceived += sink.Handle;
        var frames = CapturedFixedBeaconFrames.Select(Convert.FromHexString).ToArray();

        for (var iteration = 0; iteration < 20; iteration++) Feed(gateway, frames);
        var countBefore = sink.Count;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 100; iteration++) Feed(gateway, frames);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(countBefore + 100, sink.Count);
        Assert.Equal(10001, sink.Last.SourceId);
        Assert.Equal(990099, sink.Last.DestinationId);
        Assert.Equal(1000001, sink.Last.HotspotId);
        Assert.Equal(34.2d, sink.Last.Latitude!.Value, 10);
        Assert.Equal(108.83333333333333d, sink.Last.Longitude!.Value, 10);
    }

    [Fact]
    public void WarmedLocationQueueOperations_AllocateNoManagedBytes()
    {
        var queue = new MqttTelemetryQueue(8, 2, 64);
        var location = new DmrLocationEvent(10001, 990099, 1000001, 49.4d, 10.9d,
            0d, 133.23d, null, true, DmrLocationFormat.NmeaRmc, 123);
        for (var i = 0; i < 20; i++)
        {
            queue.EnqueueLocation(in location);
            Assert.True(queue.EventReader.TryRead(out _));
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            queue.EnqueueLocation(in location);
            if (!queue.EventReader.TryRead(out _)) throw new InvalidOperationException();
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    private static void Feed(SdsGateway gateway, byte[][] frames)
    {
        foreach (var frame in frames) gateway.HandleDataFrame(frame);
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

    private sealed class LocationSink
    {
        public int Count { get; private set; }
        public DmrLocationEvent Last { get; private set; }

        public void Handle(in DmrLocationEvent location)
        {
            Count++;
            Last = location;
        }
    }

    private sealed class LocationRouterFixture : IDisposable
    {
        private readonly RepeaterRegistry _repeaters;
        private readonly MeshDiscoveryService _mesh;
        public MicroSubnetRouter Router { get; }

        public LocationRouterFixture()
        {
            _repeaters = new RepeaterRegistry(NullLogger<RepeaterRegistry>.Instance, 100, "secret");
            var masters = new MasterRegistry(NullLogger<MasterRegistry>.Instance);
            var roaming = new RoamingRegistry(NullLogger<RoamingRegistry>.Instance, 8);
            _mesh = new MeshDiscoveryService(NullLogger<MeshDiscoveryService>.Instance, masters, roaming,
                100, 62031, 0, "mesh");
            Router = new MicroSubnetRouter(NullLogger<MicroSubnetRouter>.Instance, _repeaters, masters, roaming,
                _mesh, 100, 8, 8);
        }

        public void Dispose()
        {
            Router.Dispose();
            _mesh.Dispose();
            _repeaters.Dispose();
        }
    }
}
