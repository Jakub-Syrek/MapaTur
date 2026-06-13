using System.Globalization;
using System.Text.Json;

using MapaTur.Domain.Geography;
using MapaTur.Domain.Pois;

namespace MapaTur.Infrastructure.Pois;

/// <summary>
/// Parses Overpass <c>out:json</c> responses (produced by <c>out tags center</c>) into
/// <see cref="MountainPoi"/> aggregates. Nodes give a direct lat/lon; ways/relations use the
/// <c>center</c> point. Elements whose tags don't classify to a <see cref="PoiKind"/> are skipped.
/// </summary>
public static class OverpassPoiResponseParser
{
    /// <summary>Parses a UTF-8 Overpass response into mountain POIs.</summary>
    /// <exception cref="InvalidDataException">Thrown when the JSON is malformed or unrecognised.</exception>
    public static IReadOnlyList<MountainPoi> Parse(ReadOnlySpan<byte> utf8Json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8Json.ToArray());
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Overpass POI response is not valid JSON.", ex);
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("elements", out var elements)
                || elements.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Overpass response is missing the 'elements' array.");
            }

            // First pass: gather named place nodes (hamlet / neighbourhood / locality / …). They are NOT
            // POIs themselves — they give a nameless parking its everyday name (the parking at Brzeziny is
            // an unnamed amenity=parking in OSM; "Brzeziny" is a nearby place node).
            var places = new List<(GeoPoint Position, string Name)>();
            foreach (var element in elements.EnumerateArray())
            {
                if (element.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Object
                    && StringTag(t, "place") is not null
                    && StringTag(t, "name") is { Length: > 0 } placeName
                    && TryReadPosition(element, out GeoPoint placePos))
                {
                    places.Add((placePos, placeName));
                }
            }

            var seenIds = new HashSet<long>();
            var pois = new List<MountainPoi>();
            foreach (var element in elements.EnumerateArray())
            {
                if (TryBuildPoi(element, places, out MountainPoi poi) && seenIds.Add(poi.Id))
                {
                    pois.Add(poi);
                }
            }

            return pois;
        }
    }

    // A nameless parking borrows the name of the nearest place within this radius → "Parking {place}".
    private const double ParkingNamingRadiusMeters = 1500.0;

    private static bool TryBuildPoi(JsonElement element, IReadOnlyList<(GeoPoint Position, string Name)> places, out MountainPoi poi)
    {
        poi = null!;

        if (!element.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        if (!element.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        PoiKind? kind = PoiKindParser.FromTags(
            StringTag(tags, "tourism"),
            StringTag(tags, "amenity"),
            StringTag(tags, "shelter_type"),
            StringTag(tags, "natural"),
            StringTag(tags, "mountain_pass"));
        if (kind is null)
        {
            return false;
        }

        if (!TryReadPosition(element, out GeoPoint position))
        {
            return false;
        }

        string name = StringTag(tags, "name") ?? string.Empty;
        double? elevation = DoubleTag(tags, "ele");

        // OSM parkings are usually unnamed amenity=parking. Give them the everyday name hikers use — the
        // nearest place node (e.g. "Parking Brzeziny") — so they label on the map and are searchable as
        // route start points; with no place nearby they stay a generic "Parking".
        if (kind.Value == PoiKind.Parking && string.IsNullOrWhiteSpace(name))
        {
            string? nearest = NearestPlaceName(position, places);
            name = nearest is null ? "Parking" : $"Parking {nearest}";
        }

        poi = new MountainPoi(idElement.GetInt64(), name, position, kind.Value, elevation);
        return true;
    }

    private static string? NearestPlaceName(GeoPoint point, IReadOnlyList<(GeoPoint Position, string Name)> places)
    {
        string? best = null;
        double bestMeters = ParkingNamingRadiusMeters;
        foreach ((GeoPoint pos, string placeName) in places)
        {
            double meters = point.HaversineDistanceMetersTo(pos);
            if (meters <= bestMeters)
            {
                bestMeters = meters;
                best = placeName;
            }
        }

        return best;
    }

    private static bool TryReadPosition(JsonElement element, out GeoPoint position)
    {
        position = default;

        if (element.TryGetProperty("lat", out var lat) && element.TryGetProperty("lon", out var lon))
        {
            return TryPoint(lat, lon, out position);
        }

        if (element.TryGetProperty("center", out var center) && center.ValueKind == JsonValueKind.Object
            && center.TryGetProperty("lat", out var clat) && center.TryGetProperty("lon", out var clon))
        {
            return TryPoint(clat, clon, out position);
        }

        return false;
    }

    private static bool TryPoint(JsonElement lat, JsonElement lon, out GeoPoint position)
    {
        position = default;
        try
        {
            position = new GeoPoint(lat.GetDouble(), lon.GetDouble());
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
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

    private static double? DoubleTag(JsonElement tags, string key)
    {
        string? text = StringTag(tags, key);
        if (text is null)
        {
            return null;
        }

        // "1987", "1987 m", "1987m" — take the leading numeric run.
        int end = 0;
        while (end < text.Length && (char.IsDigit(text[end]) || text[end] is '.' or ','))
        {
            end++;
        }
        if (end == 0)
        {
            return null;
        }

        return double.TryParse(text.AsSpan(0, end).ToString().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : null;
    }
}