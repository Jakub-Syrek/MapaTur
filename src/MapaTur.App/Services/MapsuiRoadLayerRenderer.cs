using MapaTur.Domain.Geography;
using MapaTur.Domain.Trails;

using Mapsui.Layers;

using NetTopologySuite.Geometries;

using Color = Mapsui.Styles.Color;
using Map = Mapsui.Map;
using Pen = Mapsui.Styles.Pen;
using VectorStyle = Mapsui.Styles.VectorStyle;

namespace MapaTur.App.Services;

/// <summary>
/// Renders roads as a single light-grey polyline layer, separate from the colour-coded hiking trails.
/// Roads arrive as unmarked <see cref="Trail"/> geometry (see <see cref="MapaTur.Application.Roads"/>).
/// </summary>
public sealed class MapsuiRoadLayerRenderer : IRoadLayerRenderer
{
    private const string RoadLayerName = "roads-layer";
    private const string RoadColorHex = "#E5E7EB"; // light grey, distinct from the trail palette
    private const float StrokeWidthPixels = 2.0f;

    /// <inheritdoc />
    public MapBounds? CoverageBounds { get; set; }

    /// <inheritdoc />
    public void RenderRoads(Map map, IReadOnlyList<Trail> roads)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(roads);

        Clear(map);

        Geometry? clip = MapCoverageClipper.BuildClip(CoverageBounds);
        var features = roads
            .SelectMany(road => MapCoverageClipper.ToFeatures(road, clip))
            .ToList();

        if (features.Count == 0)
        {
            return;
        }

        map.Layers.Add(new MemoryLayer
        {
            Name = RoadLayerName,
            Features = features,
            Style = new VectorStyle
            {
                Line = new Pen(Color.FromString(RoadColorHex), StrokeWidthPixels),
            },
        });
    }

    /// <inheritdoc />
    public void Clear(Map map)
    {
        ArgumentNullException.ThrowIfNull(map);
        var stale = map.Layers
            .Where(layer => layer.Name == RoadLayerName)
            .ToList();
        foreach (var layer in stale)
        {
            map.Layers.Remove(layer);
        }
    }
}
