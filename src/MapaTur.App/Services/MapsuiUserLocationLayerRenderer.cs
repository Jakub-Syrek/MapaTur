using MapaTur.Domain.Location;

using Mapsui;
using Mapsui.Layers;
using Mapsui.Projections;

using Color = Mapsui.Styles.Color;
using Map = Mapsui.Map;
using Pen = Mapsui.Styles.Pen;
using SymbolStyle = Mapsui.Styles.SymbolStyle;

namespace MapaTur.App.Services;

/// <summary>
/// Renders the current GPS fix as a small filled dot on the 2D map, replacing any previous
/// marker. Follows the same single-MemoryLayer pattern as <see cref="MapsuiPoiLayerRenderer"/>
/// so the live update is just remove-then-add — Mapsui invalidates the surface on its own.
/// Accuracy halo is omitted for now (Mapsui has no native circle-of-radius primitive that
/// scales with zoom); we keep the marker visually distinct via a bright blue fill + dark
/// outline so it can never be mistaken for a POI or climbing marker.
/// </summary>
public sealed class MapsuiUserLocationLayerRenderer : IUserLocationLayerRenderer
{
    private const string LayerName = "user-location";

    /// <inheritdoc />
    public void RenderUserLocation(Map map, UserLocation? location)
    {
        ArgumentNullException.ThrowIfNull(map);

        RemoveLayer(map);

        if (location is null)
        {
            return;
        }

        var (x, y) = SphericalMercator.FromLonLat(
            location.Position.Longitude, location.Position.Latitude);
        var feature = new PointFeature(x, y);
        feature["accuracy_m"] = location.AccuracyMeters;

        // Bright iOS-blue dot with a dark outline: unambiguous against forest/rock/snow
        // tile colours, and distinct from every other marker palette on the map.
        var layer = new MemoryLayer
        {
            Name = LayerName,
            Features = new[] { feature },
            Style = new SymbolStyle
            {
                SymbolScale = 0.75,
                Outline = new Pen(Color.FromString("#0B1220"), 2.0f),
                Fill = new Mapsui.Styles.Brush(Color.FromString("#2563EB")),
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
        var stale = map.Layers
            .Where(layer => layer.Name is string name && name == LayerName)
            .ToList();
        foreach (var layer in stale)
        {
            map.Layers.Remove(layer);
        }
    }
}