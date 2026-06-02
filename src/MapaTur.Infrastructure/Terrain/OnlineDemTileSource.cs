using System.Globalization;

using MapaTur.Application.Maps;
using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Infrastructure.Terrain;

/// <summary>
/// Streams Terrarium-encoded DEM tiles from an online XYZ source (default: the AWS
/// <c>elevation-tiles-prod/terrarium</c> set) and decodes each into a <see cref="DemRaster"/>.
///
/// Fetched PNG payloads are persisted to a per-tile file cache (<c>{cacheDir}/{z}/{x}/{y}.png</c>),
/// so a tile is downloaded at most once; later requests read the cached bytes and re-decode. Mirrors
/// the online ortho base layer's fetch-and-cache approach, but yields elevation grids for the 3D mesh
/// rather than raster basemap tiles.
///
/// PNG decoding is delegated to an injected <see cref="IRasterTileDecoder"/> (the SkiaSharp codec lives
/// in the App layer); this class owns only the HTTP fetch, the on-disk cache, and the Terrarium → metres
/// conversion via <see cref="TerrariumDecoder"/>.
/// </summary>
public sealed class OnlineDemTileSource
{
    /// <summary>The AWS public Terrarium DEM tile set (z/x/y order).</summary>
    public const string DefaultUrlTemplate =
        "https://s3.amazonaws.com/elevation-tiles-prod/terrarium/{z}/{x}/{y}.png";

    // Terrarium tiles are fully populated (ocean floor included), so no sample is ever "missing".
    // Pick a sentinel below the format's representable minimum (−32768 m) so it can never collide
    // with a real decoded elevation.
    private const float NoDataSentinel = -32769f;

    private readonly HttpClient httpClient;
    private readonly IRasterTileDecoder decoder;
    private readonly string cacheDirectory;
    private readonly string urlTemplate;

    /// <summary>
    /// Initializes a new instance of the <see cref="OnlineDemTileSource"/> class.
    /// </summary>
    /// <param name="httpClient">Shared HttpClient (typically from IHttpClientFactory).</param>
    /// <param name="decoder">PNG → RGBA decoder (SkiaSharp-backed in the App layer).</param>
    /// <param name="cacheDirectory">Root directory for the on-disk tile cache.</param>
    /// <param name="urlTemplate">XYZ URL template with <c>{z}</c>/<c>{x}</c>/<c>{y}</c> placeholders.
    /// Defaults to <see cref="DefaultUrlTemplate"/>.</param>
    public OnlineDemTileSource(
        HttpClient httpClient,
        IRasterTileDecoder decoder,
        string cacheDirectory,
        string? urlTemplate = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(decoder);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);

        this.httpClient = httpClient;
        this.decoder = decoder;
        this.cacheDirectory = cacheDirectory;
        this.urlTemplate = urlTemplate ?? DefaultUrlTemplate;
    }

    /// <summary>
    /// Returns the elevation raster for <paramref name="key"/>, fetching and caching the tile on a
    /// cache miss. Returns <see langword="null"/> when the tile is unavailable (HTTP 4xx/5xx or a
    /// transient network failure) so a streaming caller can skip or retry it without faulting.
    /// </summary>
    /// <param name="key">Slippy-map tile to fetch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public async Task<DemRaster?> GetTileAsync(DemTileKey key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        byte[]? png = await ReadOrDownloadAsync(key, cancellationToken).ConfigureAwait(false);
        if (png is null)
        {
            return null;
        }

        DecodedRasterTile decoded = decoder.Decode(png);
        float[] elevations = TerrariumDecoder.Decode(decoded.Rgba, decoded.Width, decoded.Height);

        var (west, south, east, north) = SlippyTileMath.TileBounds(key.X, key.Y, key.Zoom);
        var bounds = new MapBounds(new GeoPoint(south, west), new GeoPoint(north, east));
        return new DemRaster(decoded.Width, decoded.Height, bounds, elevations, NoDataSentinel);
    }

    private async Task<byte[]?> ReadOrDownloadAsync(DemTileKey key, CancellationToken cancellationToken)
    {
        string path = CachePath(key);
        if (File.Exists(path))
        {
            return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }

        byte[]? png = await DownloadAsync(key, cancellationToken).ConfigureAwait(false);
        if (png is null)
        {
            return null;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, png, cancellationToken).ConfigureAwait(false);
        return png;
    }

    private async Task<byte[]?> DownloadAsync(DemTileKey key, CancellationToken cancellationToken)
    {
        var uri = new Uri(BuildUrl(key));
        try
        {
            using var response = await httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // 404 over the ocean, 5xx from the CDN, rate-limit, etc. — skip this tile.
                return null;
            }

            return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // DNS / connection failure — transient; let the caller retry on a later pass.
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient.Timeout surfaces as TaskCanceledException; treat as transient, not a real cancel.
            return null;
        }
    }

    private string BuildUrl(DemTileKey key) => urlTemplate
        .Replace("{z}", key.Zoom.ToString(CultureInfo.InvariantCulture))
        .Replace("{x}", key.X.ToString(CultureInfo.InvariantCulture))
        .Replace("{y}", key.Y.ToString(CultureInfo.InvariantCulture));

    private string CachePath(DemTileKey key) => Path.Combine(
        cacheDirectory,
        key.Zoom.ToString(CultureInfo.InvariantCulture),
        key.X.ToString(CultureInfo.InvariantCulture),
        $"{key.Y.ToString(CultureInfo.InvariantCulture)}.png");
}
