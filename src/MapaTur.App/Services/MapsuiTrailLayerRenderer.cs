using MapaTur.Domain.Geography;
using MapaTur.Domain.Trails;

using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Projections;

using NetTopologySuite.Geometries;

using Color = Mapsui.Styles.Color;
using Map = Mapsui.Map;
using Pen = Mapsui.Styles.Pen;
using VectorStyle = Mapsui.Styles.VectorStyle;

namespace MapaTur.App.Services;

/// <summary>
/// Renders trail polylines using Mapsui memory layers. Each PTTK color group becomes a
/// separate layer so the styling cascade stays simple and selecting by color is cheap.
/// </summary>
public sealed class MapsuiTrailLayerRenderer : ITrailLayerRenderer
{
    private const string TrailLayerPrefix = "trails-";
    private const float StrokeWidthPixels = 3.0f;

    /// <inheritdoc />
    public MapBounds? CoverageBounds { get; set; }

    /// <inheritdoc />
    public void RenderTrails(Map map, IReadOnlyList<Trail> trails)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(trails);

        RemoveExistingTrailLayers(map);

        Geometry? clip = BuildCoverageClip();

        foreach (var group in trails.GroupBy(trail => trail.PrimaryColor))
        {
            var features = group
                .SelectMany(trail => BuildFeatures(trail, clip))
                .ToList();

            if (features.Count == 0)
            {
                continue;
            }

            var layer = new MemoryLayer
            {
                Name = TrailLayerPrefix + group.Key,
                Features = features,
                Style = new VectorStyle
                {
                    Line = new Pen(Color.FromString(OsmcSymbolParser.ToHex(group.Key)), StrokeWidthPixels),
                },
            };

            map.Layers.Add(layer);
        }
    }

    // The coverage rectangle in Spherical-Mercator (or null when no coverage is set).
    private Geometry? BuildCoverageClip()
    {
        if (CoverageBounds is not { } b)
        {
            return null;
        }

        var (minX, minY) = SphericalMercator.FromLonLat(b.SouthWest.Longitude, b.SouthWest.Latitude);
        var (maxX, maxY) = SphericalMercator.FromLonLat(b.NorthEast.Longitude, b.NorthEast.Latitude);
        return new GeometryFactory().ToGeometry(new Envelope(minX, maxX, minY, maxY));
    }

    private static IEnumerable<GeometryFeature> BuildFeatures(Trail trail, Geometry? clip)
    {
        var coordinates = trail.Geometry
            .Select(point => SphericalMercator.FromLonLat(point.Longitude, point.Latitude))
            .Select(projected => new Coordinate(projected.x, projected.y))
            .ToArray();

        if (coordinates.Length < 2)
        {
            yield break;
        }

        Geometry line = new LineString(coordinates);

        if (clip is null)
        {
            yield return new GeometryFeature(line);
            yield break;
        }

        // Clip the polyline to the map coverage so nothing draws past the basemap. Intersection can
        // split a trail into several pieces (a MultiLineString) where it crosses the boundary. On the
        // rare NTS robustness error, fall back to the unclipped line.
        Geometry intersection;
        try
        {
            intersection = line.Intersection(clip);
        }
        catch (NetTopologySuite.Geometries.TopologyException)
        {
            intersection = line;
        }

        foreach (LineString piece in ExtractLineStrings(intersection))
        {
            yield return new GeometryFeature(piece);
        }
    }

    private static IEnumerable<LineString> ExtractLineStrings(Geometry geometry)
    {
        if (geometry.IsEmpty)
        {
            yield break;
        }

        switch (geometry)
        {
            case LineString ls when ls.NumPoints >= 2:
                yield return ls;
                break;
            case MultiLineString or GeometryCollection:
                for (int i = 0; i < geometry.NumGeometries; i++)
                {
                    foreach (LineString inner in ExtractLineStrings(geometry.GetGeometryN(i)))
                    {
                        yield return inner;
                    }
                }

                break;
        }
    }

    private static void RemoveExistingTrailLayers(Map map)
    {
        var stale = map.Layers
            .Where(layer => layer.Name is string name && name.StartsWith(TrailLayerPrefix, StringComparison.Ordinal))
            .ToList();
        foreach (var layer in stale)
        {
            map.Layers.Remove(layer);
        }
    }
}