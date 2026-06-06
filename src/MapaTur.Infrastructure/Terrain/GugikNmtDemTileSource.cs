using System.Globalization;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Infrastructure.Terrain;

/// <summary>
/// Streams raw 1 m elevation tiles for Poland from GUGiK's public NMT WCS and decodes each into a
/// <see cref="DemRaster"/> of metres. The service is open (no key), accepts an EPSG:3857 bbox directly
/// (so a slippy tile maps straight through with no reprojection), and returns an uncompressed float32
/// GeoTIFF that <see cref="Float32GeoTiffDecoder"/> reads with no native dependencies.
///
/// Outside Poland the source short-circuits to <c>null</c> WITHOUT any network call, so a
/// <see cref="CompositeDemTileSource"/> placed in front of the global Terrarium source falls straight
/// through to it. Fetched TIFFs are cached per tile (<c>{cacheDir}/{z}/{x}/{y}.tif</c>). Any HTTP/decode
/// failure also yields <c>null</c> rather than throwing, so streaming callers can skip or retry.
/// </summary>
public sealed class GugikNmtDemTileSource : IDemTileSource
{
    /// <summary>GUGiK NMT GRID1 WCS endpoint that serves the float32 GeoTIFF coverage (open, no key).</summary>
    public const string DefaultWcsEndpoint =
        "https://mapy.geoportal.gov.pl/wss/service/PZGIK/NMT/GRID1/WCS/DigitalTerrainModelFormatTIFF";

    /// <summary>The float32 GeoTIFF coverage in the PL-KRON86-NH vertical datum.</summary>
    public const string DefaultCoverageId = "DTM_PL-KRON86-NH_TIFF";

    // Poland WGS84 bounding box, padded a touch so border tiles are still attempted.
    private const double PolandWest = 14.0;
    private const double PolandEast = 24.2;
    private const double PolandSouth = 48.9;
    private const double PolandNorth = 55.0;

    // GUGiK marks gaps with a very-negative float (~ -3.4e38). Anything implausibly low for Polish
    // terrain (lowest point ≈ −2 m) is collapsed to this sentinel, which DemRaster excludes from its
    // elevation range and the mesh treats as a hole.
    private const float NoDataFloor = -10000f;
    private const float NoDataSentinel = -32768f;

    private readonly HttpClient httpClient;
    private readonly string cacheDirectory;
    private readonly string endpoint;
    private readonly string coverageId;
    private readonly int tileSize;

    /// <summary>Initializes the source.</summary>
    /// <param name="httpClient">Shared HttpClient (typically from IHttpClientFactory).</param>
    /// <param name="cacheDirectory">Root directory for the on-disk TIFF cache.</param>
    /// <param name="tileSize">Requested WCS grid width/height in pixels (default 256).</param>
    /// <param name="endpoint">WCS endpoint; defaults to <see cref="DefaultWcsEndpoint"/>.</param>
    /// <param name="coverageId">WCS coverage id; defaults to <see cref="DefaultCoverageId"/>.</param>
    public GugikNmtDemTileSource(
        HttpClient httpClient,
        string cacheDirectory,
        int tileSize = 256,
        string? endpoint = null,
        string? coverageId = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        if (tileSize < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(tileSize), tileSize, "Tile size must be at least 2.");
        }

        this.httpClient = httpClient;
        this.cacheDirectory = cacheDirectory;
        this.tileSize = tileSize;
        this.endpoint = endpoint ?? DefaultWcsEndpoint;
        this.coverageId = coverageId ?? DefaultCoverageId;
    }

    /// <inheritdoc />
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public async Task<DemRaster?> GetTileAsync(DemTileKey key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var (west, south, east, north) = SlippyTileMath.TileBounds(key.X, key.Y, key.Zoom);

        // Region gate: GUGiK only covers Poland — bail out fast (no HTTP) elsewhere.
        if (!IntersectsPoland(west, south, east, north))
        {
            return null;
        }

        byte[]? tiff = await ReadOrDownloadAsync(key, cancellationToken).ConfigureAwait(false);
        if (tiff is null)
        {
            return null;
        }

        Float32Grid grid;
        try
        {
            grid = Float32GeoTiffDecoder.Decode(tiff);
        }
        catch (FormatException)
        {
            // Service returned an XML ServiceException or some unexpected payload — treat as no data.
            return null;
        }

        float[] samples = SanitizeNoData(grid.Samples);
        var bounds = new MapBounds(new GeoPoint(south, west), new GeoPoint(north, east));
        return new DemRaster(grid.Width, grid.Height, bounds, samples, NoDataSentinel);
    }

    private static bool IntersectsPoland(double west, double south, double east, double north)
        => !(east < PolandWest || west > PolandEast || north < PolandSouth || south > PolandNorth);

    private static float[] SanitizeNoData(float[] samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            if (float.IsNaN(samples[i]) || samples[i] < NoDataFloor)
            {
                samples[i] = NoDataSentinel;
            }
        }

        return samples;
    }

    private async Task<byte[]?> ReadOrDownloadAsync(DemTileKey key, CancellationToken cancellationToken)
    {
        string path = CachePath(key);
        if (File.Exists(path))
        {
            return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }

        byte[]? tiff = await DownloadAsync(key, cancellationToken).ConfigureAwait(false);
        if (tiff is null)
        {
            return null;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, tiff, cancellationToken).ConfigureAwait(false);
        return tiff;
    }

    private async Task<byte[]?> DownloadAsync(DemTileKey key, CancellationToken cancellationToken)
    {
        var uri = new Uri(BuildUrl(key));
        try
        {
            using var response = await httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient.Timeout surfaces as TaskCanceledException; treat as transient, not a real cancel.
            return null;
        }
    }

    private string BuildUrl(DemTileKey key)
    {
        var inv = CultureInfo.InvariantCulture;
        var (minX, minY, maxX, maxY) = SlippyTileMath.Tile3857Bounds(key.X, key.Y, key.Zoom);
        string bbox = string.Create(inv, $"{minX},{minY},{maxX},{maxY}");
        string size = this.tileSize.ToString(inv);

        return $"{this.endpoint}?SERVICE=WCS&VERSION=1.0.0&REQUEST=GetCoverage" +
               $"&COVERAGE={this.coverageId}&CRS=EPSG:3857&BBOX={bbox}" +
               $"&WIDTH={size}&HEIGHT={size}&FORMAT=image/tiff";
    }

    private string CachePath(DemTileKey key)
    {
        var inv = CultureInfo.InvariantCulture;
        return Path.Combine(
            this.cacheDirectory,
            key.Zoom.ToString(inv),
            key.X.ToString(inv),
            $"{key.Y.ToString(inv)}.tif");
    }
}