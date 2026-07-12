using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Samples the TRUE rendered surface height — the baked 1 m tile pyramid — at a WGS-84 point, for the
/// camera's anti-tunnelling floor. The coarse base raster the floor used before is box-averaged 30 m data
/// that understates ridges by metres (the documented "base vs z16 on convex ground" gap), so a floor fed
/// from it let the eye clip into the drawn 1 m terrain ("wjazd w powierzchnię mapy"). Tiles are converted
/// to rasters once and kept in a small FIFO cache (the camera crosses a 600 m tile over many frames);
/// absent tiles are cached as absent so off-coverage probing never hammers the disk. Not thread-safe —
/// call from the render/UI thread that owns the camera.
/// </summary>
public sealed class BakedFineElevationSampler
{
    private readonly Func<DemTileKey, bool> isBaked;
    private readonly Func<DemTileKey, BakedDemTile?> loadTile;
    private readonly int zoom;
    private readonly int minZoom;
    private readonly int cacheCapacity;
    private readonly Dictionary<DemTileKey, DemRaster?> cache = new();
    private readonly Queue<DemTileKey> cacheOrder = new();

    /// <summary>
    /// Creates a sampler over a baked pyramid.
    /// </summary>
    /// <param name="isBaked">Availability predicate (same as the streaming manager's).</param>
    /// <param name="loadTile">Loads one baked tile by key; null for absent/corrupt.</param>
    /// <param name="zoom">The zoom level to sample (the finest baked level).</param>
    /// <param name="cacheCapacity">Rasters kept resident (FIFO). A handful covers the probe cluster.</param>
    /// <param name="fallbackMinZoom">Coarsest zoom to FALL BACK to when finer levels have no baked tile (or
    /// NoData) at the point. Default −1 = no fallback (sample <paramref name="zoom"/> only — the pre-z17
    /// behaviour). With the finest level raised to z17 the pyramid is z17-sparse for a long time (partial
    /// bakes, the whole pre-bake window), and without the fallback the camera floor / walk ground would lose
    /// the entire z16 surface wherever z17 is absent.</param>
    public BakedFineElevationSampler(
        Func<DemTileKey, bool> isBaked,
        Func<DemTileKey, BakedDemTile?> loadTile,
        int zoom,
        int cacheCapacity = 8,
        int fallbackMinZoom = -1)
    {
        ArgumentNullException.ThrowIfNull(isBaked);
        ArgumentNullException.ThrowIfNull(loadTile);
        ArgumentOutOfRangeException.ThrowIfLessThan(cacheCapacity, 1);
        int resolvedMinZoom = fallbackMinZoom < 0 ? zoom : fallbackMinZoom;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(resolvedMinZoom, zoom, nameof(fallbackMinZoom));
        this.isBaked = isBaked;
        this.loadTile = loadTile;
        this.zoom = zoom;
        this.minZoom = resolvedMinZoom;
        this.cacheCapacity = cacheCapacity;
    }

    /// <summary>
    /// The baked surface elevation (metres) at the point from the FINEST level that covers it — levels are
    /// probed from the configured finest zoom down to the fallback floor, so a point outside partial fine
    /// coverage still reads the coarser baked surface. Null when no probed level has a valid sample there
    /// (caller falls back to the coarse raster).
    /// </summary>
    /// <param name="longitude">WGS-84 longitude.</param>
    /// <param name="latitude">WGS-84 latitude.</param>
    public double? Sample(double longitude, double latitude)
    {
        for (int z = this.zoom; z >= this.minZoom; z--)
        {
            (int x, int y) = SlippyTileMath.LonLatToTile(longitude, latitude, z);
            var key = new DemTileKey(z, x, y);

            if (!this.cache.TryGetValue(key, out DemRaster? raster))
            {
                bool available = this.isBaked(key);
                BakedDemTile? tile = available ? this.loadTile(key) : null;
                raster = tile is not null ? BakedTileMeshBuilder.AsRaster(tile) : null;
                // Cache the result — EXCEPT an "available but load returned null" miss: with a non-blocking
                // warming loader (AsyncWarmingTileLoader) that null means "warming in the background", and
                // pinning it would freeze this level out until FIFO eviction. Retrying costs a dictionary
                // hit per probe, and the truly-absent case (isBaked false) still caches as null.
                if (!available || raster is not null)
                {
                    this.cache[key] = raster; // absent cached as null — no per-frame disk retry off-coverage
                    this.cacheOrder.Enqueue(key);
                    if (this.cacheOrder.Count > this.cacheCapacity)
                    {
                        this.cache.Remove(this.cacheOrder.Dequeue());
                    }
                }
            }

            if (raster is null)
            {
                continue;
            }

            double elevation = raster.SampleBilinear(longitude, latitude);
            if (elevation > raster.NoDataValue)
            {
                return elevation;
            }
        }

        return null;
    }
}