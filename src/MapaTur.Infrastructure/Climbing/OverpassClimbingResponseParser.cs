using System.Globalization;
using System.Text.Json;
using MapaTur.Domain.Climbing;
using MapaTur.Domain.Geography;

namespace MapaTur.Infrastructure.Climbing;

/// <summary>
/// Parses Overpass <c>out:json</c> responses produced by
/// <c>out tags center</c> into <see cref="ClimbingArea"/> aggregates. Nodes contribute a
/// direct lat/lon; ways and relations use the <c>center</c> field as a representative
/// point (sufficient for marker placement at viewport scale).
/// </summary>
public static class OverpassClimbingResponseParser
{
    /// <summary>
    /// Parses a UTF-8 encoded Overpass response into climbing areas.
    /// </summary>
    /// <param name="utf8Json">Raw response bytes.</param>
    /// <returns>Reconstructed climbing areas.</returns>
    /// <exception cref="InvalidDataException">Thrown when the JSON is malformed or unrecognised.</exception>
    public static IReadOnlyList<ClimbingArea> Parse(ReadOnlySpan<byte> utf8Json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8Json.ToArray());
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Overpass climbing response is not valid JSON.", ex);
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("elements", out var elementsArray)
                || elementsArray.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Overpass response is missing the 'elements' array.");
            }

            var seenIds = new HashSet<long>();
            var areas = new List<ClimbingArea>();

            foreach (var element in elementsArray.EnumerateArray())
            {
                if (TryBuildArea(element, out var area) && seenIds.Add(area.Id))
                {
                    areas.Add(area);
                }
            }

            return areas;
        }
    }

    private static bool TryBuildArea(JsonElement element, out ClimbingArea area)
    {
        area = null!;

        if (!element.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        long id = idElement.GetInt64();
        if (!TryReadPosition(element, out var position))
        {
            return false;
        }

        string name = string.Empty;
        string? grade = null;
        int? lengthMeters = null;
        bool? isBolted = null;

        string? climbingTag = null;
        string? climbingSportTag = null;
        string? climbingTradTag = null;
        string? climbingMultiPitchTag = null;
        string? climbingBoulderTag = null;
        string? naturalTag = null;
        string? boltedTag = null;

        if (element.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
        {
            name = StringTag(tags, "name") ?? string.Empty;
            grade = StringTag(tags, "climbing:grade:french")
                    ?? StringTag(tags, "climbing:grade:UIAA")
                    ?? StringTag(tags, "climbing:grade:yds")
                    ?? StringTag(tags, "climbing:grade");
            lengthMeters = IntTag(tags, "climbing:length");
            boltedTag = StringTag(tags, "climbing:bolted");
            climbingTag = StringTag(tags, "climbing");
            climbingSportTag = StringTag(tags, "climbing:sport");
            climbingTradTag = StringTag(tags, "climbing:trad");
            climbingMultiPitchTag = StringTag(tags, "climbing:multipitch");
            climbingBoulderTag = StringTag(tags, "climbing:boulder");
            naturalTag = StringTag(tags, "natural");

            if (boltedTag is not null)
            {
                isBolted = boltedTag.Equals("yes", StringComparison.OrdinalIgnoreCase)
                           || boltedTag.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
        }

        var type = ClimbingTypeParser.Parse(
            climbingTag,
            climbingSportTag,
            climbingTradTag,
            climbingMultiPitchTag,
            climbingBoulderTag,
            naturalTag);

        try
        {
            area = new ClimbingArea(id, name, position, type, grade, lengthMeters, isBolted);
        }
        catch (ArgumentException)
        {
            return false;
        }

        return true;
    }

    private static bool TryReadPosition(JsonElement element, out GeoPoint position)
    {
        position = default;

        if (element.TryGetProperty("lat", out var latElement)
            && element.TryGetProperty("lon", out var lonElement))
        {
            try
            {
                position = new GeoPoint(latElement.GetDouble(), lonElement.GetDouble());
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        if (element.TryGetProperty("center", out var center) && center.ValueKind == JsonValueKind.Object
            && center.TryGetProperty("lat", out var centerLat)
            && center.TryGetProperty("lon", out var centerLon))
        {
            try
            {
                position = new GeoPoint(centerLat.GetDouble(), centerLon.GetDouble());
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        return false;
    }

    private static string? StringTag(JsonElement tags, string key)
    {
        if (tags.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
        {
            string? text = value.GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        return null;
    }

    private static int? IntTag(JsonElement tags, string key)
    {
        if (tags.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
        {
            string text = value.GetString() ?? string.Empty;
            // Tag often comes as "12", "12m", "12 m" — strip trailing non-digits.
            int parsedEnd = 0;
            while (parsedEnd < text.Length && (char.IsDigit(text[parsedEnd]) || text[parsedEnd] == '.' || text[parsedEnd] == ','))
            {
                parsedEnd++;
            }
            if (parsedEnd == 0)
            {
                return null;
            }
            if (double.TryParse(text.AsSpan(0, parsedEnd).ToString().Replace(',', '.'),
                NumberStyles.Float, CultureInfo.InvariantCulture, out double numeric))
            {
                return (int)Math.Round(numeric);
            }
        }
        return null;
    }
}