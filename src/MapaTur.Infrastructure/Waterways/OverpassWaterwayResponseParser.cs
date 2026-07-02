using System.Text.Json;

using MapaTur.Application.Waterways;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Trails;

namespace MapaTur.Infrastructure.Waterways;

/// <summary>
/// Parses an Overpass <c>out geom</c> response of watercourses: <c>waterway=river|stream</c> ways become
/// unmarked <see cref="Trail"/> polylines (one per way, like the roads layer) and <c>waterway=waterfall</c>
/// nodes become <see cref="Waterfall"/> points.
/// </summary>
public static class OverpassWaterwayResponseParser
{
    /// <summary>Parses a UTF-8 encoded Overpass watercourse response.</summary>
    /// <param name="utf8Json">Raw response bytes.</param>
    /// <exception cref="InvalidDataException">Thrown when the JSON is malformed or lacks the expected shape.</exception>
    public static WaterwayFetchResult Parse(ReadOnlySpan<byte> utf8Json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8Json.ToArray());
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Overpass waterway response is not valid JSON.", ex);
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("elements", out var elements)
                || elements.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Overpass waterway response is missing the 'elements' array.");
            }

            var streams = new List<Trail>();
            var falls = new List<Waterfall>();
            foreach (var element in elements.EnumerateArray())
            {
                string? type = element.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
                if (type == "way")
                {
                    IReadOnlyList<GeoPoint> geometry = ExtractWayGeometry(element);
                    if (geometry.Count < 2)
                    {
                        continue;
                    }

                    streams.Add(new Trail(ElementId(element), ElementName(element), Array.Empty<TrailMarking>(), geometry));
                }
                else if (type == "node")
                {
                    if (!element.TryGetProperty("lat", out var latProp) || !element.TryGetProperty("lon", out var lonProp))
                    {
                        continue;
                    }

                    try
                    {
                        falls.Add(new Waterfall(
                            ElementId(element), ElementName(element),
                            new GeoPoint(latProp.GetDouble(), lonProp.GetDouble())));
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        // Degenerate coordinates — skip the node rather than aborting the response.
                    }
                }
            }

            return new WaterwayFetchResult(streams, falls);
        }
    }

    private static long ElementId(JsonElement element) =>
        element.TryGetProperty("id", out var idProp) ? idProp.GetInt64() : 0L;

    private static string ElementName(JsonElement element) =>
        element.TryGetProperty("tags", out var tags)
        && tags.TryGetProperty("name", out var nameProp)
        && nameProp.ValueKind == JsonValueKind.String
            ? nameProp.GetString() ?? string.Empty
            : string.Empty;

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