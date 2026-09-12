namespace DMRoute_ng.Integration;

internal static class LocationPrivacy
{
    private const double KilometersPerLatitudeDegree = 111.32d;

    public static void SnapToGrid(double latitude, double longitude, double gridKilometers,
        out double snappedLatitude, out double snappedLongitude)
    {
        var latitudeStep = gridKilometers / KilometersPerLatitudeDegree;
        var latitudeCell = Math.Floor((latitude + 90d) / latitudeStep);
        snappedLatitude = latitudeCell * latitudeStep + latitudeStep / 2d - 90d;
        snappedLatitude = Math.Clamp(snappedLatitude, -90d + latitudeStep / 2d, 90d - latitudeStep / 2d);

        var cosine = Math.Abs(Math.Cos(snappedLatitude * Math.PI / 180d));
        if (cosine < 1e-9d)
        {
            snappedLongitude = 0d;
            return;
        }

        var longitudeStep = gridKilometers / (KilometersPerLatitudeDegree * cosine);
        if (longitudeStep >= 360d)
        {
            snappedLongitude = 0d;
            return;
        }

        var normalizedLongitude = NormalizeLongitude(longitude);
        var longitudeCell = Math.Floor((normalizedLongitude + 180d) / longitudeStep);
        snappedLongitude = NormalizeLongitude(longitudeCell * longitudeStep + longitudeStep / 2d - 180d);
    }

    private static double NormalizeLongitude(double longitude)
    {
        var normalized = (longitude + 180d) % 360d;
        if (normalized < 0d) normalized += 360d;
        return normalized - 180d;
    }
}
