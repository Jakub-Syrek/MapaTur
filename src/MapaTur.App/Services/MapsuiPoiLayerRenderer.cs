using MapaTur.Domain.Pois;

using Mapsui;
using Mapsui.Layers;
using Mapsui.Projections;

using Color = Mapsui.Styles.Color;
using Map = Mapsui.Map;
using Pen = Mapsui.Styles.Pen;
using SymbolStyle = Mapsui.Styles.SymbolStyle;

namespace MapaTur.App.Services;

/// <summary>
/// Renders mountain POIs as colour-coded circular markers grouped by <see cref="PoiKind"/>.
/// One dedicated MemoryLayer per kind so each group inherits the right style without the
/// per-feature style indirection Mapsui's IThemeStyle would otherwise need. Mirrors
/// <see cref="MapsuiClimbingLayerRenderer"/>.
/// </summary>
public sealed class MapsuiPoiLayerRenderer : IPoiLayerRenderer
{
    private const string PoiLayerPrefix = "mountain-pois-";

    /// <inheritdoc />
    public void RenderPois(Map map, IReadOnlyList<MountainPoi> pois)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(pois);

        RemoveAllLayers(map);

        if (pois.Count == 0)
        {
            return;
        }

        foreach (var group in pois.GroupBy(poi => poi.Kind))
        {
            var features = new List<IFeature>();
            foreach (var poi in group)
            {
                var projected = SphericalMercator.FromLonLat(poi.Position.Longitude, poi.Position.Latitude);
                var feature = new PointFeature(projected.x, projected.y);
                feature["kind"] = (int)poi.Kind;
                feature["name"] = poi.Name;
                features.Add(feature);
            }

            var fillHex = "#" + PoiKindColors.ToHex(group.Key);
            var layer = new MemoryLayer
            {
                Name = PoiLayerPrefix + group.Key,
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
            .Where(layer => layer.Name is string name && name.StartsWith(PoiLayerPrefix, StringComparison.Ordinal))
            .ToList();
        foreach (var layer in stale)
        {
            map.Layers.Remove(layer);
        }
    }
}
