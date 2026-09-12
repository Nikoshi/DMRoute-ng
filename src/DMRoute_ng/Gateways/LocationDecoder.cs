using System.Buffers.Text;
using DMRoute_ng.Types;

namespace DMRoute_ng.Gateways;

internal readonly struct DecodedLocation(
    double? latitude,
    double? longitude,
    double? speedMetersPerSecond,
    double? courseDegrees,
    double? altitudeMeters,
    bool fixValid,
    DmrLocationFormat format)
{
    public double? Latitude { get; } = latitude;
    public double? Longitude { get; } = longitude;
    public double? SpeedMetersPerSecond { get; } = speedMetersPerSecond;
    public double? CourseDegrees { get; } = courseDegrees;
    public double? AltitudeMeters { get; } = altitudeMeters;
    public bool FixValid { get; } = fixValid;
    public DmrLocationFormat Format { get; } = format;
}

internal static class LocationDecoder
{
    private const double KnotsToMetersPerSecond = 0.5144444444444445d;

    public static bool TryDecodeNmeaRmc(ReadOnlySpan<byte> data, out DecodedLocation location)
    {
        var gpIndex = data.IndexOf("$GPRMC,"u8);
        var gnIndex = data.IndexOf("$GNRMC,"u8);
        var start = gpIndex < 0 ? gnIndex : gnIndex < 0 ? gpIndex : Math.Min(gpIndex, gnIndex);
        if (start < 0)
        {
            location = default;
            return false;
        }

        var sentence = data[start..];
        var checksumSeparator = sentence.IndexOf((byte)'*');
        if (checksumSeparator < 0 || checksumSeparator + 2 >= sentence.Length ||
            !TryParseHexByte(sentence.Slice(checksumSeparator + 1, 2), out var expectedChecksum))
        {
            location = default;
            return false;
        }

        byte actualChecksum = 0;
        for (var i = 1; i < checksumSeparator; i++) actualChecksum ^= sentence[i];
        if (actualChecksum != expectedChecksum)
        {
            location = default;
            return false;
        }

        var fields = sentence[..checksumSeparator];
        if (!TryGetCsvField(fields, 2, out var status) || status.Length != 1 ||
            status[0] is not ((byte)'A' or (byte)'V'))
        {
            location = default;
            return false;
        }

        var fixValid = status[0] == (byte)'A';
        double? latitude = null;
        double? longitude = null;
        if (fixValid)
        {
            if (!TryGetCsvField(fields, 3, out var latitudeField) ||
                !TryGetCsvField(fields, 4, out var latitudeHemisphere) ||
                !TryGetCsvField(fields, 5, out var longitudeField) ||
                !TryGetCsvField(fields, 6, out var longitudeHemisphere) ||
                !TryParseNmeaCoordinate(latitudeField, latitudeHemisphere, 2, 90d, out var parsedLatitude) ||
                !TryParseNmeaCoordinate(longitudeField, longitudeHemisphere, 3, 180d, out var parsedLongitude))
            {
                location = default;
                return false;
            }

            latitude = parsedLatitude;
            longitude = parsedLongitude;
        }

        double? speed = null;
        if (TryGetCsvField(fields, 7, out var speedField) && !speedField.IsEmpty)
        {
            if (!TryParseUtf8Double(speedField, out var knots) || knots < 0d)
            {
                location = default;
                return false;
            }
            speed = knots * KnotsToMetersPerSecond;
        }

        double? course = null;
        if (TryGetCsvField(fields, 8, out var courseField) && !courseField.IsEmpty)
        {
            if (!TryParseUtf8Double(courseField, out var parsedCourse) || parsedCourse is < 0d or > 360d)
            {
                location = default;
                return false;
            }
            course = parsedCourse;
        }

        location = new DecodedLocation(latitude, longitude, speed, course, null, fixValid,
            DmrLocationFormat.NmeaRmc);
        return true;
    }

    public static bool TryDecodeAnytoneGpsText(byte encoding, ReadOnlySpan<byte> text, out DecodedLocation location)
    {
        if (!LooksLikeLocationText(encoding, text))
        {
            location = default;
            return false;
        }

        var offset = text.Length >= 2 && text[0] == 0xFF && text[1] == 0xFE ? 2 : 0;
        if (!TryReadUtf16Line(text, ref offset, out var header) || !Utf16EqualsAscii(header, "Template:"u8) ||
            !TryReadUtf16Line(text, ref offset, out var latitudeLine) ||
            !TryReadUtf16Line(text, ref offset, out var longitudeLine) ||
            !TryParseUtf16Coordinate(latitudeLine, true, out var latitude) ||
            !TryParseUtf16Coordinate(longitudeLine, false, out var longitude))
        {
            location = default;
            return false;
        }

        double? speed = null;
        double? altitude = null;
        while (TryReadUtf16Line(text, ref offset, out var line))
        {
            if (Utf16StartsWithAscii(line, "V:"u8) && Utf16EndsWithAscii(line, "M/S"u8))
            {
                var value = line.Slice(4, line.Length - 10);
                if (!TryParseUtf16Double(value, out var parsedSpeed) || parsedSpeed < 0d)
                {
                    location = default;
                    return false;
                }
                speed = parsedSpeed;
            }
            else if (Utf16StartsWithAscii(line, "H:"u8) && Utf16EndsWithAscii(line, "M"u8))
            {
                var value = line.Slice(4, line.Length - 6);
                if (!TryParseUtf16Double(value, out var parsedAltitude))
                {
                    location = default;
                    return false;
                }
                altitude = parsedAltitude;
            }
        }

        location = new DecodedLocation(latitude, longitude, speed, null, altitude, true,
            DmrLocationFormat.AnytoneGpsText);
        return true;
    }

    public static bool LooksLikeLocationText(byte encoding, ReadOnlySpan<byte> text)
    {
        if (encoding != 0x04 || (text.Length & 1) != 0) return false;
        var offset = text.Length >= 2 && text[0] == 0xFF && text[1] == 0xFE ? 2 : 0;
        return TryReadUtf16Line(text, ref offset, out var header) && Utf16EqualsAscii(header, "Template:"u8);
    }

    private static bool TryParseNmeaCoordinate(
        ReadOnlySpan<byte> value, ReadOnlySpan<byte> hemisphere, int degreeDigits, double maximum,
        out double coordinate)
    {
        coordinate = 0d;
        if (value.Length <= degreeDigits || hemisphere.Length != 1 ||
            !Utf8Parser.TryParse(value[..degreeDigits], out int degrees, out var degreeBytes) ||
            degreeBytes != degreeDigits || !TryParseUtf8Double(value[degreeDigits..], out var minutes) ||
            minutes is < 0d or >= 60d)
            return false;

        coordinate = degrees + minutes / 60d;
        if (coordinate > maximum) return false;
        switch (hemisphere[0])
        {
            case (byte)'S' when degreeDigits == 2:
            case (byte)'W' when degreeDigits == 3:
                coordinate = -coordinate;
                return true;
            case (byte)'N' when degreeDigits == 2:
            case (byte)'E' when degreeDigits == 3:
                return true;
            default:
                return false;
        }
    }

    private static bool TryParseUtf16Coordinate(ReadOnlySpan<byte> line, bool latitude, out double coordinate)
    {
        coordinate = 0d;
        if (line.Length < 4 || (line.Length & 1) != 0) return false;
        var hemisphere = line[^2];
        if (line[^1] != 0 || !TryParseUtf16Double(line[..^2], out coordinate) || coordinate < 0d) return false;
        if (latitude)
        {
            if (hemisphere == (byte)'S') coordinate = -coordinate;
            else if (hemisphere != (byte)'N') return false;
            return coordinate is >= -90d and <= 90d;
        }

        if (hemisphere == (byte)'W') coordinate = -coordinate;
        else if (hemisphere != (byte)'E') return false;
        return coordinate is >= -180d and <= 180d;
    }

    private static bool TryParseUtf8Double(ReadOnlySpan<byte> value, out double parsed) =>
        Utf8Parser.TryParse(value, out parsed, out var consumed) && consumed == value.Length && double.IsFinite(parsed);

    private static bool TryParseUtf16Double(ReadOnlySpan<byte> value, out double parsed)
    {
        parsed = 0d;
        if (value.IsEmpty || (value.Length & 1) != 0) return false;
        var negative = false;
        var seenDigit = false;
        var seenDecimal = false;
        var fractionScale = 1d;
        var offset = 0;
        if (value.Length >= 2 && value[0] is (byte)'-' or (byte)'+')
        {
            negative = value[0] == (byte)'-';
            if (value[1] != 0) return false;
            offset = 2;
        }

        for (; offset < value.Length; offset += 2)
        {
            var c = value[offset];
            if (value[offset + 1] != 0) return false;
            if (c == (byte)'.' && !seenDecimal)
            {
                seenDecimal = true;
                continue;
            }
            if (c is < (byte)'0' or > (byte)'9') return false;
            seenDigit = true;
            var digit = c - (byte)'0';
            if (seenDecimal)
            {
                fractionScale *= 0.1d;
                parsed += digit * fractionScale;
            }
            else parsed = parsed * 10d + digit;
        }

        if (!seenDigit || !double.IsFinite(parsed)) return false;
        if (negative) parsed = -parsed;
        return true;
    }

    private static bool TryGetCsvField(ReadOnlySpan<byte> data, int fieldIndex, out ReadOnlySpan<byte> field)
    {
        var start = 0;
        for (var current = 0; current <= fieldIndex; current++)
        {
            if (start > data.Length) break;
            var relativeEnd = data[start..].IndexOf((byte)',');
            var end = relativeEnd < 0 ? data.Length : start + relativeEnd;
            if (current == fieldIndex)
            {
                field = data[start..end];
                return true;
            }
            if (relativeEnd < 0) break;
            start = end + 1;
        }

        field = default;
        return false;
    }

    private static bool TryParseHexByte(ReadOnlySpan<byte> value, out byte parsed)
    {
        parsed = 0;
        if (value.Length != 2 || !TryParseHexNibble(value[0], out var high) ||
            !TryParseHexNibble(value[1], out var low)) return false;
        parsed = (byte)((high << 4) | low);
        return true;
    }

    private static bool TryParseHexNibble(byte value, out byte parsed)
    {
        if (value is >= (byte)'0' and <= (byte)'9') parsed = (byte)(value - (byte)'0');
        else if (value is >= (byte)'A' and <= (byte)'F') parsed = (byte)(value - (byte)'A' + 10);
        else if (value is >= (byte)'a' and <= (byte)'f') parsed = (byte)(value - (byte)'a' + 10);
        else
        {
            parsed = 0;
            return false;
        }
        return true;
    }

    private static bool TryReadUtf16Line(ReadOnlySpan<byte> text, ref int offset, out ReadOnlySpan<byte> line)
    {
        while (offset + 1 < text.Length && text[offset + 1] == 0 &&
               text[offset] is (byte)'\r' or (byte)'\n' or 0) offset += 2;
        if (offset + 1 >= text.Length)
        {
            line = default;
            return false;
        }

        var start = offset;
        while (offset + 1 < text.Length)
        {
            if (text[offset + 1] != 0)
            {
                line = default;
                return false;
            }
            if (text[offset] is (byte)'\r' or (byte)'\n' or 0) break;
            offset += 2;
        }
        line = text[start..offset];
        return !line.IsEmpty;
    }

    private static bool Utf16EqualsAscii(ReadOnlySpan<byte> utf16, ReadOnlySpan<byte> ascii) =>
        utf16.Length == ascii.Length * 2 && Utf16StartsWithAscii(utf16, ascii);

    private static bool Utf16StartsWithAscii(ReadOnlySpan<byte> utf16, ReadOnlySpan<byte> ascii)
    {
        if (utf16.Length < ascii.Length * 2) return false;
        for (var i = 0; i < ascii.Length; i++)
            if (utf16[i * 2] != ascii[i] || utf16[i * 2 + 1] != 0) return false;
        return true;
    }

    private static bool Utf16EndsWithAscii(ReadOnlySpan<byte> utf16, ReadOnlySpan<byte> ascii)
    {
        if (utf16.Length < ascii.Length * 2) return false;
        return Utf16StartsWithAscii(utf16[(utf16.Length - ascii.Length * 2)..], ascii);
    }
}
