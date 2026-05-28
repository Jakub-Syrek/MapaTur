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
    public void RenderTrails(Map map, IReadOnlyList<Trail> trails)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(trails);

        RemoveExistingTrailLayers(map);

        foreach (var group in trails.GroupBy(trail => trail.PrimaryColor))
        {
            var features = group
                .Select(BuildFeature)
                .Where(feature => feature is not null)
                .Cast<GeometryFeature>()
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

    private static GeometryFeature? BuildFeature(Trail trail)
    {
        var coordinates = trail.Geometry
            .Select(point => SphericalMercator.FromLonLat(point.Longitude, point.Latitude))
            .Select(projected => new Coordinate(projected.x, projected.y))
            .ToArray();

        if (coordinates.Length < 2)
        {
            return null;
        }

        return new GeometryFeature(new LineString(coordinates));
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