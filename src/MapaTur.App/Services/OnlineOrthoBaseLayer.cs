using Mapsui.Tiling.Layers;

using Map = Mapsui.Map;

namespace MapaTur.App.Services;

/// <summary>
/// Adds a global online orthophoto base layer (Esri World Imagery) at the very bottom of the 2D map, so
/// the whole voivodeship — not just the bundled Tatry basemap — shows satellite imagery. Tiles are
/// fetched on demand and persisted to a local <c>FileCache</c> (hybrid online+offline): once an
/// area has been viewed it stays available without network. Detailed offline basemaps (the Tatry
/// MBTiles) stack on top where present, so existing coverage is unchanged.
/// </summary>
public static class OnlineOrthoBaseLayer
{
    /// <summary>Layer name used to find / dedupe the base layer.</summary>
    public const string LayerName = "online-ortho-base";

    /// <summary>
    /// Inserts the online ortho base at the bottom of the map (index 0) if not already present.
    /// Cached tiles are written under <paramref name="cacheDirectory"/> via <see cref="EsriOrthoTileSource"/>.
    /// </summary>
    public static void EnsureAdded(Map map, string cacheDirectory)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);

        if (map.Layers.Any(layer => layer.Name == LayerName))
        {
            return;
        }

        // Index 0 = very bottom, beneath any hillshade / Tatry basemap / vector overlays.
        map.Layers.Insert(0, new TileLayer(EsriOrthoTileSource.Create(cacheDirectory)) { Name = LayerName });
    }
}