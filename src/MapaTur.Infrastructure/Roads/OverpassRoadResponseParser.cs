using System.Text.Json;

using MapaTur.Domain.Geography;
using MapaTur.Domain.Trails;

namespace MapaTur.Infrastructure.Roads;

/// <summary>
/// Parses an Overpass <c>out geom</c> response of road ways into unmarked <see cref="Trail"/> polylines
/// (one per way). Roads carry no PTTK marking, so they render in the neutral fallback colour and live in
/// their own overlay, separate from hiking trails.
/// </summary>
public static class OverpassRoadResponseParser
{
    /// <summary>Parses a UTF-8 encoded Overpass response of <c>highway</c> ways.</summary>
    /// <param name="utf8Json">Raw response bytes.</param>
    /// <returns>One trail per way with at least two geometry points.</returns>
    /// <exception cref="InvalidDataException">Thrown when the JSON is malformed or lacks the expected shape.</exception>
    public static IReadOnlyList<Trail> Parse(ReadOnlySpan<byte> utf8Json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8Json.ToArray());
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Overpass road response is not valid JSON.", ex);
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("elements", out var elements)
                || elements.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Overpass road response is missing the 'elements' array.");
            }

            var roads = new List<Trail>();
            foreach (var element in elements.EnumerateArray())
            {
                if (!element.TryGetProperty("type", out var typeProp) || typeProp.GetString() != "way")
                {
                    continue;
                }

                IReadOnlyList<GeoPoint> geometry = ExtractWayGeometry(element);
                if (geometry.Count < 2)
                {
                    continue;
                }

                long id = element.TryGetProperty("id", out var idProp) ? idProp.GetInt64() : 0L;
                string name = string.Empty;
                if (element.TryGetProperty("tags", out var tags)
                    && tags.TryGetProperty("name", out var nameProp)
                    && nameProp.ValueKind == JsonValueKind.String)
                {
                    name = nameProp.GetString() ?? string.Empty;
                }

                roads.Add(new Trail(id, name, Array.Empty<TrailMarking>(), geometry));
            }

            return roads;
        }
    }

    private static IReadOnlyList<GeoPoint> ExtractWayGeometry(JsonElement way)
    {
        if (!way.TryGetProperty("geometry", out var geometryArray) || geometryArray.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var points = new List<GeoPoint>(geometryArray.GetArrayLength());
        foreach (var node in geometryArray.EnumerateArray())
        {
            if (!node.TryGetProperty("lat", out var latElement) || !node.TryGetProperty("lon", out var lonElement))
            {
                continue;
            }

            try
            {
                points.Add(new GeoPoint(latElement.GetDouble(), lonElement.GetDouble()));
            }
            catch (ArgumentOutOfRangeException)
            {
                // Skip degenerate coordinates rather than aborting the whole response.
            }
        }
        return points;
    }
}