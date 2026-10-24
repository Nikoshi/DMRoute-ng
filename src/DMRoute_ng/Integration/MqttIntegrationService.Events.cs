using System.Buffers.Text;
using System.Reflection;
using System.Text;
using System.Threading.Channels;
using DMRoute_ng.Configuration;
using DMRoute_ng.Gateways;
using DMRoute_ng.Registry;
using DMRoute_ng.Routing;
using DMRoute_ng.Services;
using DMRoute_ng.Types;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DMRoute_ng.Integration;

public sealed partial class MqttIntegrationService
{
    private void PublishEvent(in MqttTelemetryEvent item)
    {
        switch (item.EventType)
        {
            case MqttTelemetryEventType.Call:
                if (item.Call.EventType == DmrCallEventType.Started) PublishCallStarted(item.Call);
                else PublishCallEnded(item.Call);
                break;
            case MqttTelemetryEventType.Sms:
                PublishSms(item);
                break;
            case MqttTelemetryEventType.Location:
                PublishLocation(item.Location);
                break;
            case MqttTelemetryEventType.DiagnosticFrame:
                PublishDiagnostic(item);
                break;
        }
    }

    private void PublishCallStarted(in DmrCallEvent call)
    {
        var payload = BuildCallPayload(call, includeEnd: false, _eventPayloadBuffer);
        _mqttClient.Publish(BuildTopic(_eventTopicBuffer, "call/active"u8), payload, retain: false);
        _mqttClient.Publish(BuildEntityTopic(_eventTopicBuffer, "call/active/"u8, call.SourceId), payload, retain: true);
    }

    private void PublishCallEnded(in DmrCallEvent call)
    {
        _mqttClient.Publish(BuildEntityTopic(_eventTopicBuffer, "call/active/"u8, call.SourceId),
            ReadOnlySpan<byte>.Empty, retain: true);
        var payload = BuildCallPayload(call, includeEnd: true, _eventPayloadBuffer);
        _mqttClient.Publish(BuildTopic(_eventTopicBuffer, "call/event"u8), payload, retain: false);
    }

    private void PublishSms(in MqttTelemetryEvent item)
    {
        var slot = item.SmsMessage!;
        var textBytes = slot.Buffer.AsSpan(0, slot.Length);
        string message;
        if (item.ContainsLocation && _locationPrivacyEnabled)
            message = RedactedLocationMessage;
        else if (item.Encoding == TmsMessageWriter.Utf16LittleEndianEncoding)
        {
            if ((textBytes.Length & 1) != 0) textBytes = textBytes[..^1];
            message = Encoding.Unicode.GetString(textBytes).Trim('\0', '\r', '\n');
        }
        else message = Encoding.UTF8.GetString(textBytes).Trim('\0', '\r', '\n');

        var builder = new JsonSpanBuilder(_eventPayloadBuffer);
        builder.AppendNumber("srcId"u8, item.SourceId);
        builder.AppendNumber("dstId"u8, item.DestinationId);
        builder.AppendString("message"u8, message);
        builder.AppendTimestamp("timestamp"u8, item.OccurredAtTicks);
        builder.Finish();
        _mqttClient.Publish(BuildTopic(_eventTopicBuffer, "sds/sms"u8),
            _eventPayloadBuffer.AsSpan(0, builder.Length), retain: false);
    }

    private void PublishDiagnostic(in MqttTelemetryEvent item)
    {
        var slot = item.DiagnosticFrame!;
        Span<byte> type = stackalloc byte[4];
        type[0] = (byte)'0';
        type[1] = (byte)'x';
        const string hex = "0123456789ABCDEF";
        type[2] = (byte)hex[item.DataType >> 4];
        type[3] = (byte)hex[item.DataType & 0x0F];

        var builder = new JsonSpanBuilder(_eventPayloadBuffer);
        builder.AppendNumber("srcId"u8, item.SourceId);
        builder.AppendNumber("hotspotId"u8, item.HotspotId);
        builder.AppendString("dataTypeHex"u8, type);
        builder.AppendHexString("hexDump"u8, slot.Buffer.AsSpan(0, slot.Length));
        builder.AppendTimestamp("timestamp"u8, item.OccurredAtTicks);
        builder.Finish();
        _mqttClient.Publish(BuildTopic(_eventTopicBuffer, "diag/unknown_frame"u8),
            _eventPayloadBuffer.AsSpan(0, builder.Length), retain: false);
    }

    private void PublishLocation(in DmrLocationEvent location)
    {
        var latitude = location.Latitude;
        var longitude = location.Longitude;
        if (_locationPrivacyEnabled && latitude.HasValue && longitude.HasValue)
        {
            LocationPrivacy.SnapToGrid(latitude.Value, longitude.Value, _locationGridKilometers,
                out var snappedLatitude, out var snappedLongitude);
            latitude = snappedLatitude;
            longitude = snappedLongitude;
        }

        var builder = new JsonSpanBuilder(_eventPayloadBuffer);
        builder.AppendNumber("srcId"u8, location.SourceId);
        builder.AppendNumber("dstId"u8, location.DestinationId);
        builder.AppendNumber("hotspotId"u8, location.HotspotId);
        AppendNullableDecimal(ref builder, "lat"u8, latitude, 6);
        AppendNullableDecimal(ref builder, "lon"u8, longitude, 6);
        AppendNullableDecimal(ref builder, "speedMps"u8, location.SpeedMetersPerSecond, 3);
        AppendNullableDecimal(ref builder, "courseDeg"u8, location.CourseDegrees, 2);
        AppendNullableDecimal(ref builder, "altitudeM"u8, location.AltitudeMeters, 1);
        builder.AppendBool("fixValid"u8, location.FixValid);
        builder.AppendString("format"u8, location.Format switch
        {
            DmrLocationFormat.NmeaRmc => "NMEA_RMC"u8,
            DmrLocationFormat.AnytoneGpsText => "ANYTONE_GPS_TEXT"u8,
            _ => "UNKNOWN"u8
        });
        builder.AppendBool("obfuscated"u8, _locationPrivacyEnabled);
        if (_locationPrivacyEnabled) builder.AppendDecimal("precisionKm"u8, _locationGridKilometers, 1);
        else builder.AppendNull("precisionKm"u8);
        builder.AppendTimestamp("timestamp"u8, location.OccurredAtTicks);
        builder.Finish();
        _mqttClient.Publish(BuildTopic(_eventTopicBuffer, "sds/gps"u8),
            _eventPayloadBuffer.AsSpan(0, builder.Length), retain: false);
    }

    private static void AppendNullableDecimal(
        ref JsonSpanBuilder builder, ReadOnlySpan<byte> key, double? value, byte precision)
    {
        if (value.HasValue) builder.AppendDecimal(key, value.Value, precision);
        else builder.AppendNull(key);
    }

    private static ReadOnlySpan<byte> BuildCallPayload(
        in DmrCallEvent call, bool includeEnd, Span<byte> target)
    {
        var builder = new JsonSpanBuilder(target);
        builder.AppendNumber("srcId"u8, call.SourceId);
        builder.AppendNumber("dstId"u8, call.DestinationId);
        builder.AppendString("type"u8, call.IsGroupCall ? "GroupCall"u8 : "PrivateCall"u8);
        builder.AppendNumber("hotspotId"u8, call.HotspotId);
        if (includeEnd)
        {
            builder.AppendDecimal("durationSec"u8, call.DurationTicks / (double)TimeSpan.TicksPerSecond);
            builder.AppendString("endReason"u8,
                call.EventType == DmrCallEventType.Ended ? "Terminated"u8 : "Timeout"u8);
        }
        builder.AppendTimestamp("timestamp"u8, call.OccurredAtTicks);
        builder.Finish();
        return target[..builder.Length];
    }
}
