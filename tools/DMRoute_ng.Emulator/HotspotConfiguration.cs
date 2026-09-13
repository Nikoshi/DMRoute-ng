namespace DMRoute_ng.Emulator;

public sealed record HotspotConfiguration
{
    public string Callsign { get; init; } = "DMRTEST";
    public string RxFrequency { get; init; } = "438800000";
    public string TxFrequency { get; init; } = "438800000";
    public int TxPower { get; init; } = 1;
    public int ColorCode { get; init; } = 1;
    public string Latitude { get; init; } = "0.00000";
    public string Longitude { get; init; } = "0.00000";
    public int Height { get; init; }
    public string Location { get; init; } = "Loopback";
    public string Description { get; init; } = "DMRoute-ng Emulator";
    public string Url { get; init; } = "https://github.com/Nikoshi/DMRoute-ng";
    public string SoftwareId { get; init; } = "DMRoute-ng Emulator";
    public string PackageId { get; init; } = "Core";
}
