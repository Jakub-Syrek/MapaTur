using System.Globalization;
using System.Text.Json;

using MapaTur.Domain.Geography;
using MapaTur.Domain.Trails;

namespace MapaTur.Infrastructure.Trails.Overpass;

/// <summary>
/// Parses the JSON document returned by the Overpass API (<c>out:json</c> format) into
/// <see cref="Trail"/> aggregates. The expected response contains relations with member
/// ways and ways with inline <c>geometry</c> arrays.
/// </summary>
public static class OverpassResponseParser
{
    /// <summary>
    /// Parses a UTF-8 encoded Overpass response.
    /// </summary>
    /// <param name="utf8Json">Raw response bytes.</param>
    /// <returns>Reconstructed trails. Each input relation contributes exactly one trail.</returns>
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
            throw new InvalidDataException("Overpass response is not valid JSON.", ex);
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("elements", out var elementsArray)
                || elementsArray.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Overpass response is missing the 'elements' array.");
            }

            var ways = new Dictionary<long, IReadOnlyList<GeoPoint>>();
            var relations = new List<JsonElement>();

            foreach (var element in elementsArray.EnumerateArray())
            {
                string type = element.GetProperty("type").GetString() ?? string.Empty;
                if (type == "way")
                {
                    long id = element.GetProperty("id").GetInt64();
                    ways[id] = ExtractWayGeometry(element);
                }
                else if (type == "relation")
                {
                    relations.Add(element);
                }
            }

            var trails = new List<Trail>(relations.Count);
            foreach (var relation in relations)
            {
                if (TryBuildTrail(relation, ways, out var trail))
                {
                    trails.Add(trail);
                }
            }

            return trails;
        }
    }

    private static IReadOnlyList<GeoPoint> ExtractWayGeometry(JsonElement way)
    {
        if (!way.TryGetProperty("geometry", out var geometryArray)
            || geometryArray.ValueKind != JsonValueKind.Array)
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

    private static bool TryBuildTrail(JsonElement relation, IReadOnlyDictionary<long, IReadOnlyList<GeoPoint>> ways, out Trail trail)
    {
        long id = relation.GetProperty("id").GetInt64();

        string name = "Unnamed trail";
        string? osmcSymbol = null;
        if (relation.TryGetProperty("tags", out var tags))
        {
            if (tags.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String)
            {
                name = nameElement.GetString() ?? name;
            }
            if (tags.TryGetProperty("osmc:symbol", out var osmcElement) && osmcElement.ValueKind == JsonValueKind.String)
            {
                osmcSymbol = osmcElement.GetString();
            }
        }

        var geometry = new List<GeoPoint>();
        if (relation.TryGetProperty("members", out var members) && members.ValueKind == JsonValueKind.Array)
        {
            foreach (var member in members.EnumerateArray())
            {
                if (member.TryGetProperty("type", out var memberType)
                    && memberType.GetString() == "way"
                    && member.TryGetProperty("ref", out var refElement)
                    && ways.TryGetValue(refElement.GetInt64(), out var wayGeometry))
                {
                    geometry.AddRange(wayGeometry);
                }
            }
        }

        if (geometry.Count < 2)
        {
            trail = null!;
            return false;
        }

        var markings = new List<TrailMarking> { OsmcSymbolParser.Parse(osmcSymbol) };
        trail = new Trail(id, name, markings, geometry);
        return true;
    }
}