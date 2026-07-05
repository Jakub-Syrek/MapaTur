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
    public BakedFineElevationSampler(
        Func<DemTileKey, bool> isBaked,
        Func<DemTileKey, BakedDemTile?> loadTile,
        int zoom,
        int cacheCapacity = 8)
    {
        ArgumentNullException.ThrowIfNull(isBaked);
        ArgumentNullException.ThrowIfNull(loadTile);
        ArgumentOutOfRangeException.ThrowIfLessThan(cacheCapacity, 1);
        this.isBaked = isBaked;
        this.loadTile = loadTile;
        this.zoom = zoom;
        this.cacheCapacity = cacheCapacity;
    }

    /// <summary>
    /// The baked surface elevation (metres) at the point, or null when no baked tile covers it (caller
    /// falls back to the coarse raster) or the covering tile has NoData there.
    /// </summary>
    /// <param name="longitude">WGS-84 longitude.</param>
    /// <param name="latitude">WGS-84 latitude.</param>
    public double? Sample(double longitude, double latitude)
    {
        (int x, int y) = SlippyTileMath.LonLatToTile(longitude, latitude, this.zoom);
        var key = new DemTileKey(this.zoom, x, y);

        if (!this.cache.TryGetValue(key, out DemRaster? raster))
        {
            BakedDemTile? tile = this.isBaked(key) ? this.loadTile(key) : null;
            raster = tile is not null ? BakedTileMeshBuilder.AsRaster(tile) : null;
            this.cache[key] = raster; // absent cached as null — no per-frame disk retry off-coverage
            this.cacheOrder.Enqueue(key);
            if (this.cacheOrder.Count > this.cacheCapacity)
            {
                this.cache.Remove(this.cacheOrder.Dequeue());
            }
        }

        if (raster is null)
        {
            return null;
        }

        double elevation = raster.SampleBilinear(longitude, latitude);
        return elevation > raster.NoDataValue ? elevation : null;
    }
}