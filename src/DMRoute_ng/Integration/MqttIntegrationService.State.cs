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
    private void PublishStateSnapshot()
    {
        var now = DateTime.UtcNow.Ticks;
        PublishSystemInfo(now);
        PublishHotspots(now);
        PublishMeshPeers(now);
        PublishGuests(now);
        PublishAwayDevices(now);
        PublishActiveCalls();
    }

    private void PublishSystemInfo(long now)
    {
        var builder = new JsonSpanBuilder(_statePayloadBuffer);
        builder.AppendNumber("zoneId"u8, _zoneId);
        builder.AppendNumber("uptimeSec"u8, Math.Max(0, now - _startedAtTicks) / TimeSpan.TicksPerSecond);
        builder.AppendString("version"u8, _version);
        builder.AppendNumber("activeCalls"u8, _router.ActiveCallCount);
        builder.AppendDecimal("memoryUsageMb"u8, GC.GetTotalMemory(false) / 1048576d);
        builder.AppendNumber("droppedMqttEvents"u8, DroppedEvents);
        builder.AppendTimestamp("timestamp"u8, now);
        builder.Finish();
        _mqttClient.Publish(BuildTopic(_stateTopicBuffer, "sys/info"u8),
            _statePayloadBuffer.AsSpan(0, builder.Length), retain: true);
    }

    private void PublishHotspots(long now)
    {
        _currentHotspots.Clear();
        Span<byte> endpoint = stackalloc byte[32];
        foreach (var pair in _repeaterRegistry.GetAll())
        {
            var repeater = pair.Value;
            _currentHotspots.Add(repeater.Id);
            var endpointLength = 0;
            if (repeater.EndPoint is { } value && !value.TryFormatUtf8(endpoint, out endpointLength))
                throw new ArgumentException("Endpoint buffer is too small.");

            var lastPing = Volatile.Read(ref repeater.LastPingTicks);
            var login = Volatile.Read(ref repeater.LoggedInSinceTicks);
            var builder = new JsonSpanBuilder(_statePayloadBuffer);
            builder.AppendNumber("repeaterId"u8, repeater.Id);
            builder.AppendNumber("householdId"u8, repeater.Id / 100);
            builder.AppendString("endpoint"u8, endpoint[..endpointLength]);
            builder.AppendNumber("uptimeSec"u8, login > 0 ? Math.Max(0, now - login) / TimeSpan.TicksPerSecond : 0);
            builder.AppendString("state"u8, GetRepeaterState(repeater.State));
            builder.AppendNumber("lastPingSecAgo"u8, lastPing > 0 ? Math.Max(0, now - lastPing) / TimeSpan.TicksPerSecond : 0);
            builder.Finish();
            _mqttClient.Publish(BuildEntityTopic(_stateTopicBuffer, "routing/hotspots/"u8, repeater.Id),
                _statePayloadBuffer.AsSpan(0, builder.Length), retain: true);
        }
        PublishTombstones("routing/hotspots/"u8, _publishedHotspots, _currentHotspots);
        Swap(ref _publishedHotspots, ref _currentHotspots);
    }

    private void PublishMeshPeers(long now)
    {
        _currentPeers.Clear();
        Span<byte> endpoint = stackalloc byte[32];
        foreach (var pair in _masterRegistry.GetAll())
        {
            var peer = pair.Value;
            _currentPeers.Add(peer.ZoneId);
            if (!peer.DataEndPoint.TryFormatUtf8(endpoint, out var endpointLength))
                throw new ArgumentException("Endpoint buffer is too small.");
            var lastSeen = Volatile.Read(ref peer.LastSeenTicks);
            var builder = new JsonSpanBuilder(_statePayloadBuffer);
            builder.AppendNumber("zoneId"u8, peer.ZoneId);
            builder.AppendString("endpoint"u8, endpoint[..endpointLength]);
            builder.AppendNumber("lastSeenSecAgo"u8, Math.Max(0, now - lastSeen) / TimeSpan.TicksPerSecond);
            builder.AppendString("status"u8, "Online"u8);
            builder.Finish();
            _mqttClient.Publish(BuildEntityTopic(_stateTopicBuffer, "routing/mesh/peers/"u8, peer.ZoneId),
                _statePayloadBuffer.AsSpan(0, builder.Length), retain: true);
        }
        PublishTombstones("routing/mesh/peers/"u8, _publishedPeers, _currentPeers);
        Swap(ref _publishedPeers, ref _currentPeers);
    }

    private void PublishGuests(long now)
    {
        _currentGuests.Clear();
        var count = _roamingRegistry.CopyLocalGuests(_guestSnapshots);
        for (var i = 0; i < count; i++)
        {
            var guest = _guestSnapshots[i];
            _currentGuests.Add(guest.DeviceId);
            var builder = new JsonSpanBuilder(_statePayloadBuffer);
            builder.AppendNumber("deviceId"u8, guest.DeviceId);
            builder.AppendNumber("homeZone"u8, guest.DeviceId / 100);
            builder.AppendNumber("connectedViaHotspot"u8, guest.HotspotId);
            builder.AppendNumber("activeSinceMin"u8, Math.Max(0, now - guest.ActiveSinceTicks) / TimeSpan.TicksPerMinute);
            builder.AppendNumber("lastSeenSecAgo"u8, Math.Max(0, now - guest.LastSeenTicks) / TimeSpan.TicksPerSecond);
            builder.Finish();
            _mqttClient.Publish(BuildEntityTopic(_stateTopicBuffer, "routing/roaming/guests/"u8, guest.DeviceId),
                _statePayloadBuffer.AsSpan(0, builder.Length), retain: true);
        }
        PublishTombstones("routing/roaming/guests/"u8, _publishedGuests, _currentGuests);
        Swap(ref _publishedGuests, ref _currentGuests);
    }

    private void PublishAwayDevices(long now)
    {
        _currentAway.Clear();
        var count = _roamingRegistry.CopyAwayDevices(_awaySnapshots);
        for (var i = 0; i < count; i++)
        {
            var away = _awaySnapshots[i];
            _currentAway.Add(away.DeviceId);
            var builder = new JsonSpanBuilder(_statePayloadBuffer);
            builder.AppendNumber("deviceId"u8, away.DeviceId);
            builder.AppendNumber("currentForeignZone"u8, away.CurrentZoneId);
            builder.AppendNumber("lastUpdateSecAgo"u8, Math.Max(0, now - away.LastSeenTicks) / TimeSpan.TicksPerSecond);
            builder.Finish();
            _mqttClient.Publish(BuildEntityTopic(_stateTopicBuffer, "routing/roaming/away/"u8, away.DeviceId),
                _statePayloadBuffer.AsSpan(0, builder.Length), retain: true);
        }
        PublishTombstones("routing/roaming/away/"u8, _publishedAway, _currentAway);
        Swap(ref _publishedAway, ref _currentAway);
    }

    private void PublishActiveCalls()
    {
        _currentCalls.Clear();
        var count = _router.CopyActiveCalls(_callSnapshots);
        for (var i = 0; i < count; i++)
        {
            var call = _callSnapshots[i];
            _currentCalls.Add(call.SourceId);
            var eventValue = new DmrCallEvent(call.SourceId, call.DestinationId, call.HotspotId,
                call.IsGroupCall, DmrCallEventType.Started, call.StartedAtTicks);
            var payload = BuildCallPayload(eventValue, includeEnd: false, _statePayloadBuffer);
            _mqttClient.Publish(BuildEntityTopic(_stateTopicBuffer, "call/active/"u8, call.SourceId),
                payload, retain: true);
        }
        PublishTombstones("call/active/"u8, _publishedCalls, _currentCalls);
        Swap(ref _publishedCalls, ref _currentCalls);
    }

    private void PublishTombstones(ReadOnlySpan<byte> subtree, HashSet<int> previous, HashSet<int> current)
    {
        foreach (var id in previous)
        {
            if (!current.Contains(id))
                _mqttClient.Publish(BuildEntityTopic(_stateTopicBuffer, subtree, id), ReadOnlySpan<byte>.Empty, retain: true);
        }
    }

    private ReadOnlySpan<byte> BuildTopic(Span<byte> target, ReadOnlySpan<byte> subtree)
    {
        var offset = BuildTopicPrefix(target);
        subtree.CopyTo(target[offset..]);
        return target[..(offset + subtree.Length)];
    }

    private ReadOnlySpan<byte> BuildEntityTopic(Span<byte> target, ReadOnlySpan<byte> subtree, int id)
    {
        var offset = BuildTopicPrefix(target);
        subtree.CopyTo(target[offset..]);
        offset += subtree.Length;
        if (!Utf8Formatter.TryFormat(id, target[offset..], out var written))
            throw new ArgumentException("MQTT topic buffer is too small.");
        return target[..(offset + written)];
    }

    private int BuildTopicPrefix(Span<byte> target)
    {
        "dmroute/"u8.CopyTo(target);
        var offset = 8;
        if (!Utf8Formatter.TryFormat(_zoneId, target[offset..], out var written))
            throw new ArgumentException("MQTT topic buffer is too small.");
        offset += written;
        target[offset++] = (byte)'/';
        return offset;
    }
}
