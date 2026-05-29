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
                BuildTrailsForRelation(relation, ways, trails);
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

    // OSM points share the same node when ways meet at an endpoint, so an exact
    // float compare is usually enough. The tolerance handles tiny precision drift
    // from the JSON round-trip — 1e-7 deg ≈ 1 cm on the ground.
    private const double EndpointMatchTolerance = 1e-7;

    private static void BuildTrailsForRelation(JsonElement relation, IReadOnlyDictionary<long, IReadOnlyList<GeoPoint>> ways, List<Trail> output)
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

        if (!relation.TryGetProperty("members", out var members) || members.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        // Walk the relation's ways in order, stitching consecutive ways whose
        // endpoints match. Whenever two adjacent ways DON'T connect — a real
        // discontinuity in the OSM data — flush the current segment as its own
        // trail and start fresh. Without this split the renderer drew a long
        // straight "jumper" between the two disconnected pieces, which is what
        // showed up as suspect red lines across half the country.
        var currentSegment = new List<GeoPoint>();
        var marking = OsmcSymbolParser.Parse(osmcSymbol);
        int segmentIndex = 0;

        foreach (var member in members.EnumerateArray())
        {
            if (!(member.TryGetProperty("type", out var memberType)
                  && memberType.GetString() == "way"
                  && member.TryGetProperty("ref", out var refElement)
                  && ways.TryGetValue(refElement.GetInt64(), out var wayGeometry)))
            {
                continue;
            }
            if (wayGeometry.Count == 0)
            {
                continue;
            }

            if (currentSegment.Count == 0)
            {
                currentSegment.AddRange(wayGeometry);
                continue;
            }

            var lastInSegment = currentSegment[^1];
            var firstOfWay = wayGeometry[0];
            var lastOfWay = wayGeometry[^1];

            if (EndpointsMatch(lastInSegment, firstOfWay))
            {
                // Way picks up where the segment left off — drop the duplicate junction.
                for (int i = 1; i < wayGeometry.Count; i++)
                {
                    currentSegment.Add(wayGeometry[i]);
                }
            }
            else if (EndpointsMatch(lastInSegment, lastOfWay))
            {
                // Way runs the opposite direction; reverse it onto the segment.
                for (int i = wayGeometry.Count - 2; i >= 0; i--)
                {
                    currentSegment.Add(wayGeometry[i]);
                }
            }
            else
            {
                // Genuine discontinuity. Flush what we have and start a new segment.
                FlushSegment(currentSegment, id, segmentIndex++, name, marking, output);
                currentSegment = new List<GeoPoint>(wayGeometry);
            }
        }

        FlushSegment(currentSegment, id, segmentIndex, name, marking, output);
    }

    private static void FlushSegment(List<GeoPoint> segment, long relationId, int segmentIndex, string name, TrailMarking marking, List<Trail> output)
    {
        if (segment.Count < 2)
        {
            return;
        }

        // Synthesize a unique id when a relation contributed multiple segments so the
        // repository's primary-key upsert doesn't collide.
        long id = segmentIndex == 0 ? relationId : (relationId * 1000L) + segmentIndex;
        var markings = new List<TrailMarking> { marking };
        output.Add(new Trail(id, name, markings, segment));
    }

    private static bool EndpointsMatch(GeoPoint a, GeoPoint b)
    {
        return Math.Abs(a.Latitude - b.Latitude) < EndpointMatchTolerance
            && Math.Abs(a.Longitude - b.Longitude) < EndpointMatchTolerance;
    }
}