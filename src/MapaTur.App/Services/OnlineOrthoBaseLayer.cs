using BruTile;
using BruTile.Cache;
using BruTile.Predefined;
using BruTile.Web;

using Mapsui.Tiling.Layers;

using Map = Mapsui.Map;

namespace MapaTur.App.Services;

/// <summary>
/// Adds a global online orthophoto base layer (Esri World Imagery) at the very bottom of the 2D map, so
/// the whole voivodeship — not just the bundled Tatry basemap — shows satellite imagery. Tiles are
/// fetched on demand and persisted to a local <see cref="FileCache"/> (hybrid online+offline): once an
/// area has been viewed it stays available without network. Detailed offline basemaps (the Tatry
/// MBTiles) stack on top where present, so existing coverage is unchanged.
/// </summary>
public static class OnlineOrthoBaseLayer
{
    /// <summary>Layer name used to find / dedupe the base layer.</summary>
    public const string LayerName = "online-ortho-base";

    // Esri World Imagery XYZ endpoint (z/y/x order). Free for development use; global orthophoto.
    private const string EsriWorldImageryUrl =
        "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}";

    /// <summary>
    /// Inserts the online ortho base at the bottom of the map (index 0) if not already present.
    /// Cached tiles are written under <paramref name="cacheDirectory"/>.
    /// </summary>
    public static void EnsureAdded(Map map, string cacheDirectory)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);

        if (map.Layers.Any(layer => layer.Name == LayerName))
        {
            return;
        }

        var cache = new FileCache(Path.Combine(cacheDirectory, "ortho-esri"), "jpg");
        var schema = new GlobalSphericalMercator(YAxis.OSM, 0, 19, "EsriWorldImagery");
        var source = new HttpTileSource(
            schema,
            EsriWorldImageryUrl,
            serverNodes: null,
            apiKey: null,
            name: "Esri World Imagery",
            persistentCache: cache,
            attribution: new Attribution(
                "Esri, Maxar, Earthstar Geographics",
                "https://www.esri.com/"),
            configureHttpRequestMessage: request =>
                request.Headers.UserAgent.ParseAdd("MapaTur/1.0 (+https://github.com/Jakub-Syrek/MapaTur)"));

        // Index 0 = very bottom, beneath any hillshade / Tatry basemap / vector overlays.
        map.Layers.Insert(0, new TileLayer(source) { Name = LayerName });
    }
}