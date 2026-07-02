using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Terrain;

/// <summary>Progress of a region load: <see cref="Completed"/> of <see cref="Total"/> tiles fetched.</summary>
/// <param name="Completed">Tiles whose fetch has finished (whether they returned data or not).</param>
/// <param name="Total">Total tiles planned for the region.</param>
public readonly record struct RegionLoadProgress(int Completed, int Total);

/// <summary>
/// Loads a whole region's elevation as one <see cref="DemRaster"/> by planning the slippy tiles that
/// cover a <see cref="MapBounds"/> at a zoom (<see cref="DemTilePlanner"/>), fetching them through an
/// <see cref="IDemTileSource"/> (e.g. a composite of GUGiK NMT 1 m + global Terrarium) with bounded
/// concurrency, and stitching the results with <see cref="DemTileMosaic"/>. Tiles that come back null
/// (out of coverage / failed) are skipped; the region is null only when no tile could be fetched.
/// </summary>
public sealed class OnlineRegionDemLoader
{
    private readonly IDemTileSource source;
    private readonly int maxConcurrentFetches;

    // Decoded-tile cache (opt-in): the detail re-center re-loaded the SAME z16 window every camera move, so the
    // expensive per-tile fetch+decode ran over ~all unchanged tiles every time. Caching the DECODED per-tile
    // raster by its slippy key lets a sliding window decode only the NEWLY-entered tiles and reuse the overlap;
    // each call still stitches into a FRESH pool-rented output buffer (the render-thread buffer model is intact —
    // the stitched mosaic is a transient consumed by the build before the render thread ever sees a buffer). The
    // cached rasters are immutable after decode (Stitch only Array.Copies out of them), so sharing is safe. The
    // cache is round-scoped: tiles not requested in a load are evicted so a roaming window stays bounded.
    private readonly bool cacheDecodedTiles;
    private readonly Dictionary<DemTileKey, DemRaster> decodedCache = new();
    private readonly HashSet<DemTileKey> usedThisRound = new();
    private readonly object cacheGate = new();

    /// <summary>Decoded tiles currently held in the cache (0 when caching is off).</summary>
    public int DecodedTileCount
    {
        get { lock (this.cacheGate) { return this.decodedCache.Count; } }
    }

    /// <summary>Cache hits in the most recent load (tiles reused without a fetch/decode).</summary>
    public int LastDecodedTileHits { get; private set; }

    /// <summary>Cache misses in the most recent load (tiles actually fetched/decoded).</summary>
    public int LastDecodedTileMisses { get; private set; }

    /// <summary>Initializes the loader over a tile source.</summary>
    /// <param name="source">The DEM tile source (often a <see cref="CompositeDemTileSource"/>).</param>
    /// <param name="maxConcurrentFetches">Max tiles fetched at once. Bounded to stay a good citizen of
    /// the shared national WCS while still beating a one-at-a-time crawl. Default 6.</param>
    /// <param name="cacheDecodedTiles">When true, decoded per-tile rasters are cached by slippy key and reused
    /// across loads, so a sliding detail window only decodes newly-entered tiles. Off by default (the one-shot
    /// region loads — offline download, the coarse base — gain nothing and would just hold memory).</param>
    public OnlineRegionDemLoader(IDemTileSource source, int maxConcurrentFetches = 6, bool cacheDecodedTiles = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (maxConcurrentFetches < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxConcurrentFetches), maxConcurrentFetches, "Concurrency must be at least 1.");
        }

        this.source = source;
        this.maxConcurrentFetches = maxConcurrentFetches;
        this.cacheDecodedTiles = cacheDecodedTiles;
    }

    /// <summary>
    /// Fetches and stitches the tiles covering <paramref name="bounds"/> at <paramref name="zoom"/>.
    /// Returns <c>null</c> when no tile was available.
    /// </summary>
    /// <param name="bounds">Geographic extent to cover.</param>
    /// <param name="zoom">Slippy zoom level to fetch at.</param>
    /// <param name="progress">Optional reporter; fires once per tile as fetches finish.</param>
    /// <param name="tileAvailable">Optional gate: when set, only tiles it returns <c>true</c> for are
    /// fetched; the rest are skipped without ever reaching the source. Pass a cache-presence check to keep
    /// a render loop offline-deterministic (no network fetch while flying); returns null when nothing
    /// passes the gate, so the caller can fall back to a coarser base.</param>
    /// <param name="fillNoData">When true (default), coverage gaps and the Slovak-border clip are filled so
    /// a NoData-unaware mesh extends flat to the edge. The LOD render path passes false so its NoData-aware
    /// mesh can hole those cells through to the coarse base instead of rendering flat geometry over them.</param>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public async Task<DemRaster?> LoadRegionAsync(
        MapBounds bounds,
        int zoom,
        IProgress<RegionLoadProgress>? progress = null,
        Func<DemTileKey, bool>? tileAvailable = null,
        bool fillNoData = true,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DemTileKey> planned = DemTilePlanner.TilesForBounds(bounds, zoom);
        List<DemTileKey> keys;
        if (tileAvailable is null)
        {
            keys = new List<DemTileKey>(planned);
        }
        else
        {
            keys = new List<DemTileKey>(planned.Count);
            foreach (DemTileKey planKey in planned)
            {
                if (tileAvailable(planKey))
                {
                    keys.Add(planKey);
                }
            }
        }

        int total = keys.Count;

        if (this.cacheDecodedTiles)
        {
            lock (this.cacheGate)
            {
                this.usedThisRound.Clear();
            }

            this.LastDecodedTileHits = 0;
            this.LastDecodedTileMisses = 0;
        }

        using var gate = new SemaphoreSlim(this.maxConcurrentFetches);
        int completed = 0;
        var tasks = new List<Task<PlacedDemTile?>>(keys.Count);
        foreach (DemTileKey key in keys)
        {
            tasks.Add(FetchAsync(
                key,
                gate,
                () => progress?.Report(new RegionLoadProgress(Interlocked.Increment(ref completed), total)),
                cancellationToken));
        }

        PlacedDemTile?[] fetched = await Task.WhenAll(tasks).ConfigureAwait(false);

        // Bound the cache to roughly the live window: drop any decoded tile not requested this load. Safe here —
        // all fetches above have completed (Task.WhenAll), so no in-flight FetchAsync is still reading the cache.
        if (this.cacheDecodedTiles)
        {
            EvictUnusedDecodedTiles();
        }

        var placed = new List<PlacedDemTile>(fetched.Length);
        foreach (PlacedDemTile? tile in fetched)
        {
            if (tile is not null)
            {
                placed.Add(tile);
            }
        }

        if (placed.Count == 0)
        {
            return null;
        }

        DemRaster stitched = DemTileMosaic.Stitch(placed);
        // FillNoData (coverage gaps / a bbox clipping the Slovak border) lets a NoData-unaware mesh extend
        // flat to the edge. The LOD render path opts OUT so its NoData-aware mesh can hole those cells to the
        // base instead of spiking flat geometry over them.
        return fillNoData ? DemRasterRepair.FillNoData(stitched) : stitched;
    }

    private async Task<PlacedDemTile?> FetchAsync(
        DemTileKey key, SemaphoreSlim gate, Action onFetched, CancellationToken cancellationToken)
    {
        // Cache fast-path: a decoded tile already held for this key is reused without touching the source or the
        // concurrency gate — the whole point of the cache (a re-center re-fetched all the unchanged overlap). The
        // decoded raster is immutable after decode, so returning the shared instance is safe.
        if (this.cacheDecodedTiles)
        {
            lock (this.cacheGate)
            {
                this.usedThisRound.Add(key);
                if (this.decodedCache.TryGetValue(key, out DemRaster? cached))
                {
                    this.LastDecodedTileHits++;
                    onFetched();
                    return new PlacedDemTile(key, cached);
                }
            }
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DemRaster? raster = await this.source.GetTileAsync(key, cancellationToken).ConfigureAwait(false);
            if (raster is null)
            {
                return null; // unavailable tiles are NOT cached, so a transient miss can be retried next load
            }

            if (this.cacheDecodedTiles)
            {
                lock (this.cacheGate)
                {
                    this.decodedCache[key] = raster;
                    this.LastDecodedTileMisses++;
                }
            }

            return new PlacedDemTile(key, raster);
        }
        finally
        {
            gate.Release();
            onFetched();
        }
    }

    // Drops decoded tiles not requested in the current load, so a window roaming across the region keeps the cache
    // bounded to roughly one window. Eviction is ZOOM-SCOPED: only tiles at a zoom that was requested THIS load are
    // candidates, so the one singleton loader can serve the z16 detail re-center AND the z13 base/legacy-patch
    // loads without each zoom's load wiping the other's tiles (which would thrash the cache to uselessness).
    private void EvictUnusedDecodedTiles()
    {
        lock (this.cacheGate)
        {
            if (this.decodedCache.Count == this.usedThisRound.Count)
            {
                return;
            }

            HashSet<int>? zoomsThisRound = null;
            foreach (DemTileKey key in this.usedThisRound)
            {
                (zoomsThisRound ??= new HashSet<int>()).Add(key.Zoom);
            }

            if (zoomsThisRound is null)
            {
                return; // nothing requested this round ⇒ keep what we have (another zoom owns it)
            }

            List<DemTileKey>? gone = null;
            foreach (DemTileKey key in this.decodedCache.Keys)
            {
                if (zoomsThisRound.Contains(key.Zoom) && !this.usedThisRound.Contains(key))
                {
                    (gone ??= new List<DemTileKey>()).Add(key);
                }
            }

            if (gone is not null)
            {
                foreach (DemTileKey key in gone)
                {
                    this.decodedCache.Remove(key);
                }
            }
        }
    }
}