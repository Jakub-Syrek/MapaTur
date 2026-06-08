using BruTile;
using BruTile.Cache;
using BruTile.Predefined;
using BruTile.Web;

namespace MapaTur.App.Services;

/// <summary>
/// Single source of truth for the Esri World Imagery orthophoto tile source (global, z0–19), backed by the
/// shared <c>ortho-esri</c> <see cref="FileCache"/>. Used both by the 2D base layer
/// (<see cref="OnlineOrthoBaseLayer"/>) and by the 3D high-zoom near-field ortho streaming, so both fetch and
/// cache through the same on-disk store.
/// </summary>
public static class EsriOrthoTileSource
{
    /// <summary>Esri World Imagery XYZ endpoint (z/y/x order). Free for development use; global orthophoto.</summary>
    private const string EsriWorldImageryUrl =
        "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}";

    /// <summary>
    /// Builds the Esri World Imagery tile source persisting to <c>{cacheDirectory}/ortho-esri/{z}/{x}/{y}.jpg</c>.
    /// Tiles are fetched on demand and reused from disk on later views (hybrid online+offline).
    /// </summary>
    public static HttpTileSource Create(string cacheDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);

        var cache = new FileCache(Path.Combine(cacheDirectory, "ortho-esri"), "jpg");
        var schema = new GlobalSphericalMercator(YAxis.OSM, 0, 19, "EsriWorldImagery");
        return new HttpTileSource(
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
    }
}