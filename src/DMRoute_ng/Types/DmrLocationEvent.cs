namespace DMRoute_ng.Types;

public enum DmrLocationFormat : byte
{
    NmeaRmc,
    AnytoneGpsText
}

public readonly struct DmrLocationEvent(
    int sourceId,
    int destinationId,
    int hotspotId,
    double? latitude,
    double? longitude,
    double? speedMetersPerSecond,
    double? courseDegrees,
    double? altitudeMeters,
    bool fixValid,
    DmrLocationFormat format,
    long occurredAtTicks)
{
    public int SourceId { get; } = sourceId;
    public int DestinationId { get; } = destinationId;
    public int HotspotId { get; } = hotspotId;
    public double? Latitude { get; } = latitude;
    public double? Longitude { get; } = longitude;
    public double? SpeedMetersPerSecond { get; } = speedMetersPerSecond;
    public double? CourseDegrees { get; } = courseDegrees;
    public double? AltitudeMeters { get; } = altitudeMeters;
    public bool FixValid { get; } = fixValid;
    public DmrLocationFormat Format { get; } = format;
    public long OccurredAtTicks { get; } = occurredAtTicks;
}
