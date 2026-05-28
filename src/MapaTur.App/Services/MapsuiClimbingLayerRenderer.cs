using MapaTur.Domain.Climbing;

using Mapsui;
using Mapsui.Layers;
using Mapsui.Projections;

using Color = Mapsui.Styles.Color;
using Map = Mapsui.Map;
using Pen = Mapsui.Styles.Pen;
using SymbolStyle = Mapsui.Styles.SymbolStyle;

namespace MapaTur.App.Services;

/// <summary>
/// Renders climbing areas as colour-coded circular markers grouped by
/// <see cref="ClimbingType"/>. One dedicated MemoryLayer per type so each group
/// inherits the right style without the per-feature style indirection Mapsui's
/// IThemeStyle would otherwise need.
/// </summary>
public sealed class MapsuiClimbingLayerRenderer : IClimbingLayerRenderer
{
    private const string ClimbingLayerPrefix = "climbing-areas-";

    /// <inheritdoc />
    public void RenderClimbingAreas(Map map, IReadOnlyList<ClimbingArea> areas)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(areas);

        RemoveAllLayers(map);

        if (areas.Count == 0)
        {
            return;
        }

        foreach (var group in areas.GroupBy(area => area.Type))
        {
            var features = new List<IFeature>();
            foreach (var area in group)
            {
                var projected = SphericalMercator.FromLonLat(area.Position.Longitude, area.Position.Latitude);
                var feature = new PointFeature(projected.x, projected.y);
                feature["type"] = (int)area.Type;
                feature["name"] = area.Name;
                features.Add(feature);
            }

            var fillHex = "#" + ClimbingTypeColors.ToHex(group.Key);
            var layer = new MemoryLayer
            {
                Name = ClimbingLayerPrefix + group.Key,
                Features = features,
                Style = new SymbolStyle
                {
                    SymbolScale = 0.55,
                    Outline = new Pen(Color.FromString("#1F2937"), 1.5f),
                    Fill = new Mapsui.Styles.Brush(Color.FromString(fillHex)),
                },
            };

            map.Layers.Add(layer);
        }
    }

    /// <inheritdoc />
    public void Clear(Map map)
    {
        ArgumentNullException.ThrowIfNull(map);
        RemoveAllLayers(map);
    }

    private static void RemoveAllLayers(Map map)
    {
        var stale = map.Layers
            .Where(layer => layer.Name is string name && name.StartsWith(ClimbingLayerPrefix, StringComparison.Ordinal))
            .ToList();
        foreach (var layer in stale)
        {
            map.Layers.Remove(layer);
        }
    }
}