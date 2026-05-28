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
/// Renders climbing areas as colored circular markers on a dedicated Mapsui layer.
/// Boulder problems are orange, sport routes are red, trad routes are blue,
/// multi-pitches are purple, crags / cliffs are grey.
/// </summary>
public sealed class MapsuiClimbingLayerRenderer : IClimbingLayerRenderer
{
    private const string ClimbingLayerName = "climbing-areas";

    /// <inheritdoc />
    public void RenderClimbingAreas(Map map, IReadOnlyList<ClimbingArea> areas)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(areas);

        RemoveLayer(map);

        if (areas.Count == 0)
        {
            return;
        }

        var features = new List<IFeature>(areas.Count);
        foreach (var area in areas)
        {
            var projected = SphericalMercator.FromLonLat(area.Position.Longitude, area.Position.Latitude);
            var feature = new PointFeature(projected.x, projected.y);
            feature["type"] = (int)area.Type;
            feature["name"] = area.Name;
            features.Add(feature);
        }

        var layer = new MemoryLayer
        {
            Name = ClimbingLayerName,
            Features = features,
            Style = new SymbolStyle
            {
                SymbolScale = 0.55,
                Outline = new Pen(Color.FromString("#1F2937"), 1.5f),
                Fill = new Mapsui.Styles.Brush(Color.FromString("#E11D48")),
            },
        };

        map.Layers.Add(layer);
    }

    /// <inheritdoc />
    public void Clear(Map map)
    {
        ArgumentNullException.ThrowIfNull(map);
        RemoveLayer(map);
    }

    private static void RemoveLayer(Map map)
    {
        var existing = map.Layers.FirstOrDefault(layer => layer.Name == ClimbingLayerName);
        if (existing is not null)
        {
            map.Layers.Remove(existing);
        }
    }
}