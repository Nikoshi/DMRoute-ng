using System.Collections.Concurrent;
using System.Threading.Channels;
using DMRoute_ng.Coding;
using DMRoute_ng.Configuration;
using DMRoute_ng.Core;
using DMRoute_ng.Registry;
using DMRoute_ng.Routing;
using DMRoute_ng.Types;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DMRoute_ng.Services;

public sealed partial class RadioCheckReachabilityCoordinator
{
    private async Task SendRadioCheckAsync(ulong commandId, int sourceId, int targetId, CancellationToken token)
    {
        if (!_router.TryGetAnyLocalTarget(targetId, out var route)) return;
        var burst = new byte[DmrBurstWriter.BurstLength];
        var packet = new byte[DmrBurstWriter.DmrdLength];
        for (var preamble = 5; preamble > 0; preamble--)
        {
            DmrBurstWriter.WriteCsbkPreamble(
                burst, sourceId, targetId, (byte)preamble, route.ColorCode, isDataContent: false);
            SendControlBurst(packet, burst, sourceId, targetId, in route);
            await Task.Delay(BurstDelay, _timeProvider, token).ConfigureAwait(false);
        }
        DmrBurstWriter.WriteRadioCheckRequest(burst, sourceId, targetId, route.ColorCode);
        SendControlBurst(packet, burst, sourceId, targetId, in route);
    }

    private void SendControlBurst(
        Span<byte> packet,
        ReadOnlySpan<byte> burst,
        int sourceId,
        int targetId,
        in RadioTargetRoute route)
    {
        DmrBurstWriter.WriteDmrd(
            packet,
            burst,
            unchecked((byte)Interlocked.Increment(ref _packetSequence)),
            sourceId,
            targetId,
            route.HotspotId,
            0x03,
            unchecked((uint)Interlocked.Increment(ref _streamSequence)));
        _sender.SendTo(packet, route.EndPoint);
    }

    private void QueueFallback(int sourceId, int targetId, long nowTicks)
    {
        if (!_settings.FallbackSdsEnabled) return;
        var entry = new RadioCheckOutboxEntry
        {
            MessageId = NextCorrelationId(),
            OriginZone = _zoneId,
            HomeZone = targetId / 100,
            SourceId = sourceId,
            TargetId = targetId,
            CreatedAtTicks = nowTicks,
            ExpiresAtTicks = nowTicks + TimeSpan.FromHours(_settings.FallbackTtlHours).Ticks
        };
        if (!_outbox.TryAdd(in entry)) Interlocked.Increment(ref _droppedFallbacks);
    }

    private void ProcessOutbox()
    {
        if (!_settings.FallbackSdsEnabled) return;
        var now = _timeProvider.GetUtcNow().Ticks;
        foreach (var entry in _outbox.Snapshot(now))
        {
            // Die Ursprungszone übergibt fremde Teilnehmer an deren Heimat-Master.
            // Erst der Heimat-Master entscheidet zwischen lokaler und Roaming-Zustellung.
            if (entry.HomeZone != _zoneId)
            {
                if (now - entry.LastAttemptTicks < TransferRetryDelay.Ticks ||
                    !_masters.TryGet(entry.HomeZone, out var home)) continue;
                var transfer = ToFallbackMessage(MeshControlKind.FallbackTransfer, entry, _zoneId);
                if (_mesh.QueueControlMessage(in transfer, home.DataEndPoint)) _outbox.MarkAttempt(entry.MessageId, now);
                continue;
            }

            if (now - entry.LastAttemptTicks < DeliveryRetryDelay.Ticks) continue;
            if (_router.TryGetAnyLocalTarget(entry.TargetId, out var local))
            {
                var request = new FallbackSdsRequest(entry.MessageId, _zoneId, entry.SourceId, entry.TargetId, local);
                if (_fallbackSds.TryQueue(in request)) _outbox.MarkAttempt(entry.MessageId, now);
            }
            else if (_roaming.TryGetRoamedDeviceZone(entry.TargetId, out var zone) &&
                     _masters.TryGet(zone, out var serving))
            {
                var delivery = ToFallbackMessage(MeshControlKind.FallbackDeliver, entry, _zoneId);
                if (_mesh.QueueControlMessage(in delivery, serving.DataEndPoint)) _outbox.MarkAttempt(entry.MessageId, now);
            }
        }
    }

    private void AcceptFallback(in MeshControlMessage message, Ipv4Endpoint remote)
    {
        if (message.TargetId / 100 != _zoneId || message.ExpiresAtTicks <= _timeProvider.GetUtcNow().Ticks) return;
        var entry = new RadioCheckOutboxEntry
        {
            MessageId = message.CorrelationId,
            OriginZone = message.OriginZone,
            HomeZone = _zoneId,
            SourceId = message.SourceId,
            TargetId = message.TargetId,
            CreatedAtTicks = message.CreatedAtTicks,
            ExpiresAtTicks = message.ExpiresAtTicks
        };
        if (!_outbox.TryAdd(in entry)) return;
        var accepted = ToFallbackMessage(MeshControlKind.FallbackAccepted, entry, _zoneId);
        _mesh.QueueControlMessage(in accepted, remote);
    }

    private void DeliverRemoteFallback(in MeshControlMessage message)
    {
        if (!TryRememberCommand(message.CorrelationId, _timeProvider.GetUtcNow().Ticks)) return;
        if (!_router.TryGetAnyLocalTarget(message.TargetId, out var local)) return;
        var request = new FallbackSdsRequest(
            message.CorrelationId, message.OriginZone, message.SourceId, message.TargetId, local);
        _fallbackSds.TryQueue(in request);
    }

    private void ProcessDelivery(in FallbackSdsDelivery delivery)
    {
        // Nur der Besitzer der Outbox entfernt den dauerhaften Eintrag. Eine
        // ausführende Roaming-Zone meldet das Ergebnis deshalb an die Besitzerzone.
        if (delivery.OwnerZone == _zoneId)
        {
            if (delivery.Delivered) _outbox.Remove(delivery.MessageId);
            return;
        }
        if (!_masters.TryGet(delivery.OwnerZone, out var owner)) return;
        var result = new MeshControlMessage(
            MeshControlKind.FallbackResult,
            delivery.MessageId,
            _zoneId,
            0,
            0,
            Status: delivery.Delivered ? MeshRouteStatus.Found : MeshRouteStatus.NotFound);
        _mesh.QueueControlMessage(in result, owner.DataEndPoint);
    }

    private static MeshControlMessage ToFallbackMessage(
        MeshControlKind kind,
        RadioCheckOutboxEntry entry,
        int senderZone) => new(
        kind,
        entry.MessageId,
        senderZone,
        entry.SourceId,
        entry.TargetId,
        CreatedAtTicks: entry.CreatedAtTicks,
        ExpiresAtTicks: entry.ExpiresAtTicks);
}
