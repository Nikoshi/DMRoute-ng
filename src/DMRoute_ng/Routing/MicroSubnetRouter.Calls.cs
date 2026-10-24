using System.Buffers.Binary;
using DMRoute_ng.Coding;
using DMRoute_ng.Core;
using DMRoute_ng.Registry;
using DMRoute_ng.Services;
using DMRoute_ng.Types;
using Microsoft.Extensions.Logging;

namespace DMRoute_ng.Routing;

public sealed partial class MicroSubnetRouter
{
    public int CopyActiveCalls(Span<ActiveCallSnapshot> destination)
    {
        var count = 0;
        lock (_activeCallsLock)
        {
            foreach (var pair in _activeCalls)
            {
                if (count == destination.Length) break;
                var state = pair.Value;
                destination[count++] = new ActiveCallSnapshot(
                    pair.Key,
                    state.DstId,
                    state.HotspotId,
                    state.IsGroupCall,
                    state.StartTicks);
            }
        }
        return count;
    }

    private void HandleSignaling(
        ReadOnlySpan<byte> packet,
        int srcId,
        int dstId,
        int hotspotId,
        bool isGroupCall,
        byte dataType)
    {
        var now = DateTime.UtcNow.Ticks;
        var publishStart = false;

        switch (dataType)
        {
            case 0x01:
                lock (_activeCallsLock)
                {
                    if (_activeCalls.TryGetValue(srcId, out var existing))
                    {
                        _activeCalls[srcId] = new CallState(
                            dstId,
                            hotspotId,
                            isGroupCall,
                            existing.StartTicks,
                            now);
                    }
                    else if (_activeCalls.Count < _maxActiveCalls)
                    {
                        _activeCalls.Add(srcId, new CallState(dstId, hotspotId, isGroupCall, now, now));
                        publishStart = true;
                    }
                    else
                    {
                        Interlocked.Increment(ref _droppedCallStates);
                    }
                }

                if (publishStart)
                {
                    LogCallStart(_logger, isGroupCall ? "GroupCall" : "PrivateCall", srcId, dstId);
                    var callEvent = new DmrCallEvent(
                        srcId,
                        dstId,
                        hotspotId,
                        isGroupCall,
                        DmrCallEventType.Started,
                        now);
                    OnCallEvent?.Invoke(in callEvent);
                }
                break;
            case 0x02:
                lock (_activeCallsLock)
                {
                    if (_activeCalls.TryGetValue(srcId, out var active))
                        _activeCalls[srcId] = new CallState(
                            active.DstId,
                            active.HotspotId,
                            active.IsGroupCall,
                            active.StartTicks,
                            now,
                            now,
                            true);
                }
                break;
            case 0x03:
                LogCsbk(_logger, srcId, dstId);
                break;
            case <= 0x08:
                lock (_activeCallsLock)
                {
                    if (_activeCalls.TryGetValue(srcId, out var current))
                        _activeCalls[srcId] = new CallState(
                            current.DstId,
                            current.HotspotId,
                            current.IsGroupCall,
                            current.StartTicks,
                            now,
                            current.TerminationTicks,
                            current.PendingTermination);
                }
                break;
            default:
                OnUnknownFrameReceived?.Invoke(packet, srcId, hotspotId, dataType, now);
                break;
        }
    }

    private void CleanupStaleCalls(object? state)
    {
        var currentTicks = DateTime.UtcNow.Ticks;
        var count = 0;

        lock (_activeCallsLock)
        {
            foreach (var pair in _activeCalls)
            {
                var call = pair.Value;
                var elapsed = currentTicks - call.LastFrameTicks;
                var eventType = call.PendingTermination && elapsed > TimeSpan.FromMilliseconds(1500).Ticks
                    ? (byte)0x02
                    : !call.PendingTermination && elapsed > TimeSpan.FromSeconds(3).Ticks
                        ? (byte)0xFE
                        : (byte)0;
                if (eventType != 0) _expiredCalls[count++] = new ExpiredCall(pair.Key, call, eventType);
            }

            for (var i = 0; i < count; i++) _activeCalls.Remove(_expiredCalls[i].SourceId);
        }

        for (var i = 0; i < count; i++)
        {
            var expired = _expiredCalls[i];
            var endedAtTicks = expired.EventType == 0x02
                ? expired.State.TerminationTicks
                : expired.State.LastFrameTicks;
            var durationTicks = Math.Max(0, endedAtTicks - expired.State.StartTicks);
            if (expired.EventType == 0x02)
                LogCallEnd(_logger, expired.State.IsGroupCall ? "GroupCall" : "PrivateCall", expired.SourceId, expired.State.DstId);
            else
                LogCallTimeout(_logger, expired.SourceId, expired.State.DstId);
            var callEvent = new DmrCallEvent(
                expired.SourceId,
                expired.State.DstId,
                expired.State.HotspotId,
                expired.State.IsGroupCall,
                expired.EventType == 0x02 ? DmrCallEventType.Ended : DmrCallEventType.TimedOut,
                expired.EventType == 0x02 ? endedAtTicks : currentTicks,
                durationTicks);
            OnCallEvent?.Invoke(in callEvent);
            _expiredCalls[i] = default;
        }
    }
}
