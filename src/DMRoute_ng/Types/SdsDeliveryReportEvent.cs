using DMRoute_ng.Gateways;

namespace DMRoute_ng.Types;

public readonly record struct SdsDeliveryReportEvent(
    int SourceId,
    int DestinationId,
    int HotspotId,
    bool IsGroupCall,
    TmsDeliveryStatus Status,
    long CompletedAtTicks);
