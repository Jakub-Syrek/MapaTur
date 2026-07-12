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

    // Empty-tile reject floor: GUGiK returns a valid-structured but ALL-ZERO raster on no-coverage or a
    // flaky/overloaded response. Any real Polish terrain in a tile tops out far above this; an empty tile
    // tops out at ~0. A tile whose highest valid elevation is below this is treated as "no tile" and NOT
    // cached, so a transient zero self-heals on a later fetch instead of becoming permanent cache poison.
    private const double EmptyTileFloorMeters = 1.0;

    // Anti-washboard: when a tile covers a lot of ground, requesting it at the plain tile grid makes the
    // WCS resample its 1 m source on a coarse grid (≈19 m/px at z13) across an EPSG:2180→3857 reprojection,
    // baking a diagonal ripple into the heights (whose derivative — the lighting normal — then stripes the
    // foothills). We over-request at ~this resolution where the server resamples gently, then area-average
    // back down to tileSize. Detail tiles (already near native 1 m) resolve to factor 1 and are untouched.
    private const double TargetMetersPerPixel = 5.0;
    private const int MaxSupersampleFactor = 1; // supersampler OFF (user decision): the over-request + downsample baked a moiré ring-grid ("paski") into the base; factor 1 = native, clean. Base re-fetches once (new cache name).

    // SUB-NATIVE supersampling (2026-07-10, TILE-PRODUCTION §2.5): at z≥17 the request grid (0.78 m/px) is
    // FINER than the 1 m source and the WCS's own resample bakes a grid-locked weave into the heights
    // ("strukturka" on flats, organ-pipe flutes on walls; the self-sampled Slovak DMR5 tiles are clean —
    // weave 0.02 vs PL 0.14+). Over-request ×2 (512 px — the server only UPSAMPLES here, no coarse-grid
    // resample, so the §B1 moiré case does not apply) and Gaussian-downsample ourselves through the existing
    // LowPassDownsample path. Cached as {y}_512.tif; the legacy {y}.tif is read as a FALLBACK when the
    // download fails, which keeps the injected (non-redownloadable) Slovak DMR5 tiles alive — the §B1
    // orphaned-cache lesson applied deliberately instead of tripped over.
    private const int SubNativeSupersampleZoom = 17;
    private const int SubNativeSupersampleFactor = 2;

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

        int fetchPx = FetchPixelsFor(key);
        string path = CachePath(key);
        bool fromCache = File.Exists(path);

        // Read from cache if present; otherwise download — but do NOT write to the cache yet. The tile is
        // committed to disk only AFTER it decodes and passes the empty-tile guard below, so a GUGiK all-zero
        // response is never cached. A ServiceException/XML payload decodes to null the same way.
        byte[]? tiff = fromCache
            ? await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false)
            : await DownloadAsync(key, fetchPx, cancellationToken).ConfigureAwait(false);
        Float32Grid? decoded = TryDecode(tiff);

        // Legacy-name fallback: a supersampled zoom writes {y}_{px}.tif, but tiles that can never be
        // (re)downloaded — the injected Slovak DMR5 z17 set — live under the legacy {y}.tif name. When the
        // fresh fetch yields nothing usable, serve the legacy file instead of orphaning it (§B1 lesson).
        bool fromLegacy = false;
        if (decoded is null && !fromCache)
        {
            string legacyPath = LegacyCachePath(key);
            if (!string.Equals(legacyPath, path, StringComparison.Ordinal) && File.Exists(legacyPath))
            {
                decoded = TryDecode(
                    await File.ReadAllBytesAsync(legacyPath, cancellationToken).ConfigureAwait(false));
                fromLegacy = decoded is not null;
            }
        }

        if (decoded is not { } grid)
        {
            return null;
        }

        float[] samples = SanitizeNoData(grid.Samples);
        var bounds = new MapBounds(new GeoPoint(south, west), new GeoPoint(north, east));

        // If we over-requested (factor > 1), area-average the dense buffer back to tileSize — a proper
        // low-pass that removes the WCS coarse-grid washboard without adding mesh vertices. Only when the
        // server actually returned the dense grid we asked for; otherwise pass the decoded raster through.
        DemRaster raster;
        int factor = fetchPx / this.tileSize;
        if (factor > 1 && grid.Width == fetchPx && grid.Height == fetchPx)
        {
            // ZERO-VOID MASK (the "<=0.5 = void!" lesson, supersampling edition — regression 2026-07-11):
            // GUGiK marks out-of-coverage with literal flat-0, which the sentinel-aware Gaussian below would
            // treat as VALID and smear into real terrain (100–900 m garbage the bake's FillNarrowZeroStrips
            // can no longer recognise as a strip). Mask ≤0.5 to the sentinel BEFORE the low-pass, restore the
            // 0 marker AFTER, so the repair chain sees exactly the flat-0 semantics it always had. (Caveat:
            // ties this branch to highland products — real ground ≤0.5 m exists only far outside the current
            // z17 footprint; the bake's HoleBelow(100) carries the same assumption.)
            for (int i = 0; i < samples.Length; i++)
            {
                if (samples[i] != NoDataSentinel && samples[i] <= 0.5f)
                {
                    samples[i] = NoDataSentinel;
                }
            }

            // Gaussian (overlapping) downsample, not a plain box: the box left a moiré ring-grid ("obwódki")
            // on the base — disjoint blocks can't smooth across boundaries and the box passes WCS ripple that
            // aliases. Gaussian low-pass at the output Nyquist removes it without flattening real terrain. Same
            // factor/fetchPx → same cache key, so this needs NO re-fetch of already-cached tiles.
            float[] averaged = MapaTur.Application.Terrain.DemTileSupersampler.LowPassDownsample(
                samples, this.tileSize, factor, NoDataSentinel);
            for (int i = 0; i < averaged.Length; i++)
            {
                if (averaged[i] == NoDataSentinel)
                {
                    averaged[i] = 0f; // restore the flat-0 void marker for the repair chain
                }
            }

            raster = new DemRaster(this.tileSize, this.tileSize, bounds, averaged, NoDataSentinel);
        }
        else
        {
            raster = new DemRaster(grid.Width, grid.Height, bounds, samples, NoDataSentinel);
        }

        // Empty-tile guard (the "plasticine" root cause): GUGiK returns a valid-structured but ALL-ZERO raster
        // on no-coverage or a flaky/overloaded response. Reject it and DO NOT cache it — the cache is keyed on
        // File.Exists, so a committed zero is pinned forever and never re-fetched, leaving the blunt base on
        // screen. Keeping it uncached lets a later fetch self-heal a transient zero; a genuinely empty tile (no
        // PL 1 m coverage) just re-resolves to the base on each load, exactly as the render path already does.
        if (!DemRasterCoverage.HasTerrain(raster, EmptyTileFloorMeters))
        {
            return null;
        }

        // Valid tile: persist a freshly downloaded one. The cache stores the RAW WCS bytes; any area-average
        // above is re-applied on read, so the cache key/content stays stable. A legacy-fallback read never
        // persists — the legacy file already IS the cache.
        if (!fromCache && !fromLegacy)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, tiff!, cancellationToken).ConfigureAwait(false);
        }

        return raster;
    }

    // Decode that treats any unexpected payload (XML ServiceException, truncated body) as "no data".
    private static Float32Grid? TryDecode(byte[]? tiff)
    {
        if (tiff is null)
        {
            return null;
        }

        try
        {
            return Float32GeoTiffDecoder.Decode(tiff);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    // The pre-supersampling cache name ({y}.tif) — where the injected Slovak DMR5 tiles (and the raw
    // pre-2026-07-10 PL z17 fetches) live. Read-only fallback; never written to.
    private string LegacyCachePath(DemTileKey key)
    {
        var inv = CultureInfo.InvariantCulture;
        return Path.Combine(
            this.cacheDirectory, key.Zoom.ToString(inv), key.X.ToString(inv), $"{key.Y.ToString(inv)}.tif");
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

    private async Task<byte[]?> DownloadAsync(DemTileKey key, int fetchPx, CancellationToken cancellationToken)
    {
        var uri = new Uri(BuildUrl(key, fetchPx));
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

    private string BuildUrl(DemTileKey key, int fetchPx)
    {
        var inv = CultureInfo.InvariantCulture;
        var (minX, minY, maxX, maxY) = SlippyTileMath.Tile3857Bounds(key.X, key.Y, key.Zoom);
        string bbox = string.Create(inv, $"{minX},{minY},{maxX},{maxY}");
        string size = fetchPx.ToString(inv);

        return $"{this.endpoint}?SERVICE=WCS&VERSION=1.0.0&REQUEST=GetCoverage" +
               $"&COVERAGE={this.coverageId}&CRS=EPSG:3857&BBOX={bbox}" +
               $"&WIDTH={size}&HEIGHT={size}&FORMAT=image/tiff";
    }

    // Over-request factor for this tile's ground size, so the WCS resamples gently (see TargetMetersPerPixel).
    // Deterministic per tile (depends only on zoom), so the cache key stays stable.
    private int FetchPixelsFor(DemTileKey key)
    {
        if (key.Zoom >= SubNativeSupersampleZoom)
        {
            return this.tileSize * SubNativeSupersampleFactor; // sub-native anti-weave (see the consts above)
        }

        var (minX, _, maxX, _) = SlippyTileMath.Tile3857Bounds(key.X, key.Y, key.Zoom);
        int factor = MapaTur.Application.Terrain.DemTileSupersampler.SupersampleFactor(
            maxX - minX, this.tileSize, TargetMetersPerPixel, MaxSupersampleFactor);
        return this.tileSize * factor;
    }

    /// <summary>
    /// True when <paramref name="key"/>'s TIFF is already on disk, i.e. it can be served without any
    /// network call. Lets an offline-first layer (download planner / LOD) decide cache vs fetch without
    /// reaching for the file system itself. Pure presence check — does not validate the file's contents.
    /// </summary>
    public bool IsCached(DemTileKey key) => File.Exists(CachePath(key));

    private string CachePath(DemTileKey key)
    {
        var inv = CultureInfo.InvariantCulture;
        int fetchPx = FetchPixelsFor(key);
        // Supersampled (anti-washboard) tiles get a resolution suffix so they don't collide with an older
        // coarse-grid entry. Tiles fetched at the plain tile size keep the LEGACY name ({y}.tif) so the
        // existing offline detail cache (z16 etc., downloaded before supersampling) is still found — otherwise
        // the cache-only LOD streamer can't load the 1 m detail and the coarse base shows through striped.
        string name = fetchPx == this.tileSize
            ? $"{key.Y.ToString(inv)}.tif"
            : $"{key.Y.ToString(inv)}_{fetchPx.ToString(inv)}.tif";
        return Path.Combine(this.cacheDirectory, key.Zoom.ToString(inv), key.X.ToString(inv), name);
    }
}