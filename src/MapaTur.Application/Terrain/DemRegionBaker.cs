using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Terrain;

/// <summary>Outcome of a region bake: how many tiles were written and the total bytes on disk.</summary>
/// <param name="TilesWritten">Number of baked tiles written across all requested zoom levels.</param>
/// <param name="BytesWritten">Total size of the written tiles in bytes.</param>
public readonly record struct BakeRegionResult(int TilesWritten, long BytesWritten);

/// <summary>Progress of a region bake: <see cref="Completed"/> of <see cref="Total"/> tiles written.</summary>
/// <param name="Completed">Tiles written so far (across all zoom levels).</param>
/// <param name="Total">Total tiles planned across all requested zoom levels.</param>
public readonly record struct BakeProgress(int Completed, int Total);

/// <summary>
/// Offline (no-GUI) driver that pre-bakes a region of the DEM pyramid to disk. For the FINEST requested
/// zoom it fetches each covering tile TOGETHER WITH A ONE-TILE NEIGHBOUR MARGIN, stitches the block, and
/// bakes the centre tile with the full per-tile repair over that margin (<see cref="DemTileBaker.BakeWithMargin"/>)
/// so adjacent tiles' shared edges come out bit-identical (no cracks/walls at the seam — the neighbour-aware
/// FillNarrowZeroStrips/FillPits passes see real neighbour context, exactly as the live whole-window repair
/// does). Each COARSER level is derived by NoData-aware box-average downsample (<see cref="BakedDemDownsampler"/>)
/// of the level one step finer, so the whole pyramid is a single repair pass plus cheap averaging. Tiles are
/// written to <c>{outputDir}/{zoom}/{x}/{y}.bdt</c> (<see cref="BakedDemTileStore"/>). This is a dev/maintenance
/// entry point — it is NOT invoked on app start and does not touch the live render path.
/// </summary>
public sealed class DemRegionBaker
{
    private readonly IDemTileSource source;
    private readonly int maxConcurrentFetches;
    private readonly DemRaster? baseDem;

    /// <summary>Initializes the baker over a tile source.</summary>
    /// <param name="source">The DEM tile source the finest level is baked from (e.g. a composite GUGiK + Terrarium).</param>
    /// <param name="maxConcurrentFetches">Max source tiles fetched at once for the finest level. Default 6.</param>
    /// <param name="baseDem">Coarse base DEM (e.g. the whole-Tatra <c>tatry.dem</c>) used to backfill the finest
    /// tiles' NoData voids — out-of-coverage holes punched by <see cref="DemRasterRepair.HoleBelow"/> plus
    /// watercourse/border voids — so the baked mesh shows base-height terrain there instead of see-through gaps,
    /// matching the live detail path. Null skips the backfill (coverage holes stay NoData → the render base shows
    /// through). The same base every live load uses, so the bake reproduces the live surface.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxConcurrentFetches"/> is below 1.</exception>
    public DemRegionBaker(IDemTileSource source, int maxConcurrentFetches = 6, DemRaster? baseDem = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrentFetches, 1);
        this.source = source;
        this.maxConcurrentFetches = maxConcurrentFetches;
        this.baseDem = baseDem;
    }

    /// <summary>
    /// Bakes every tile covering <paramref name="bounds"/> at each level in <paramref name="zoomLevels"/> and
    /// writes them under <paramref name="outputDir"/>. The finest level is baked from the source; coarser
    /// levels are downsampled from the level one step finer (so coarser levels in <paramref name="zoomLevels"/>
    /// require their finer neighbour to also be present in the list — pass a contiguous descending run such as
    /// 16,15,14,13).
    /// </summary>
    /// <param name="bounds">Geographic extent to bake.</param>
    /// <param name="zoomLevels">Zoom levels to produce (any order; processed finest-first).</param>
    /// <param name="outputDir">Root directory for the <c>{zoom}/{x}/{y}.bdt</c> tree.</param>
    /// <param name="progress">Optional reporter; fires as tiles are written.</param>
    /// <param name="cancellationToken">Cancels the bake.</param>
    /// <returns>The tile count and total bytes written.</returns>
    /// <exception cref="ArgumentNullException">A required parameter is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="zoomLevels"/> is empty.</exception>
    public async Task<BakeRegionResult> BakeRegionAsync(
        MapBounds bounds,
        IReadOnlyList<int> zoomLevels,
        string outputDir,
        IProgress<BakeProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(zoomLevels);
        ArgumentNullException.ThrowIfNull(outputDir);
        if (zoomLevels.Count == 0)
        {
            throw new ArgumentException("At least one zoom level is required.", nameof(zoomLevels));
        }

        int[] zooms = zoomLevels.Distinct().OrderByDescending(z => z).ToArray();
        int total = zooms.Sum(z => DemTilePlanner.TilesForBounds(bounds, z).Count);

        int written = 0;
        long bytes = 0;
        void Report() => progress?.Report(new BakeProgress(written, total));

        // Finest level: fetch + repair from source.
        int finest = zooms[0];
        Dictionary<DemTileKey, BakedDemTile> finerLevel = await BakeFinestLevelAsync(
            bounds, finest, outputDir, () => { written++; }, b => bytes += b, Report, cancellationToken)
            .ConfigureAwait(false);

        // Coarser levels: each derived by downsampling the level one step finer.
        for (int i = 1; i < zooms.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int zoom = zooms[i];
            int finerZoom = zooms[i - 1];
            int step = 1 << (finerZoom - zoom); // tiles-per-axis of the finer level inside one coarse tile

            var thisLevel = new Dictionary<DemTileKey, BakedDemTile>();
            foreach (DemTileKey coarseKey in DemTilePlanner.TilesForBounds(bounds, zoom))
            {
                cancellationToken.ThrowIfCancellationRequested();
                BakedDemTile? coarse = BuildCoarseTile(coarseKey, finerLevel, step);
                if (coarse is null)
                {
                    continue; // no finer coverage under this coarse tile
                }

                long size = WriteTile(outputDir, coarse);
                bytes += size;
                written++;
                thisLevel[coarseKey] = coarse;
                Report();
            }

            finerLevel = thisLevel;
        }

        return new BakeRegionResult(written, bytes);
    }

    private async Task<Dictionary<DemTileKey, BakedDemTile>> BakeFinestLevelAsync(
        MapBounds bounds,
        int zoom,
        string outputDir,
        Action onWritten,
        Action<long> addBytes,
        Action report,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<DemTileKey> keys = DemTilePlanner.TilesForBounds(bounds, zoom);
        var baked = new Dictionary<DemTileKey, BakedDemTile>(keys.Count);

        using var gate = new SemaphoreSlim(this.maxConcurrentFetches);
        var fetches = new List<Task<BakedDemTile?>>(keys.Count);
        foreach (DemTileKey key in keys)
        {
            fetches.Add(FetchAndBakeAsync(key, gate, cancellationToken));
        }

        BakedDemTile?[] results = await Task.WhenAll(fetches).ConfigureAwait(false);

        // Write serially after all fetches complete (deterministic order, single-threaded IO).
        foreach (BakedDemTile? tile in results)
        {
            if (tile is null)
            {
                continue;
            }

            long size = WriteTile(outputDir, tile);
            addBytes(size);
            onWritten();
            baked[tile.Key] = tile;
            report();
        }

        return baked;
    }

    private async Task<BakedDemTile?> FetchAndBakeAsync(
        DemTileKey key, SemaphoreSlim gate, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Fetch the tile PLUS its one-tile neighbour ring (3×3 block). The centre tile must exist; a missing
            // neighbour leaves NoData in the margin (same as a window-edge tile in the live streamer). The
            // neighbour-aware repairs then see real context up to a full tile past the core edge, so the cropped
            // core agrees with its neighbours on the shared boundary — eliminating the seam walls/cracks.
            DemRaster? centre = await this.source.GetTileAsync(key, cancellationToken).ConfigureAwait(false);
            if (centre is null)
            {
                return null; // no 1 m coverage for this tile — the render base carries it
            }

            int tileW = centre.Columns;
            int tileH = centre.Rows;

            // Gather the 3×3 block keyed by (dx, dy) ∈ {-1,0,1}², centre at (0,0).
            var block = new DemRaster?[3, 3];
            block[1, 1] = centre;
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0)
                    {
                        continue;
                    }

                    var nKey = new DemTileKey(key.Zoom, key.X + dx, key.Y + dy);
                    DemRaster? n = await this.source.GetTileAsync(nKey, cancellationToken).ConfigureAwait(false);
                    if (n is not null && (n.Columns != tileW || n.Rows != tileH))
                    {
                        n = null; // mismatched neighbour can't tile cleanly — treat as absent (NoData margin)
                    }

                    block[dx + 1, dy + 1] = n;
                }
            }

            DemRaster window = StitchNeighbourBlock(block, tileW, tileH, (float)centre.NoDataValue);
            // The centre tile sits at offset (tileW, tileH) inside the fixed 3×3 window (the -1 column/row are
            // always present in the buffer even when the neighbour data is NoData).
            return DemTileBaker.BakeWithMargin(window, tileW, tileH, tileW, tileH, key, this.baseDem);
        }
        finally
        {
            gate.Release();
        }
    }

    // Lays a 3×3 block of equal-sized tile rasters (some possibly null = NoData) into one (3·tileW)×(3·tileH)
    // window, NoData-filled where a tile is absent. The centre tile is always present; its core therefore sits
    // at a FIXED (tileW, tileH) offset regardless of which neighbours exist. Allocates a plain array (this runs
    // concurrently and offline — it deliberately does NOT touch the live render's MeshBufferPool).
    private static DemRaster StitchNeighbourBlock(DemRaster?[,] block, int tileW, int tileH, float noData)
    {
        int cols = tileW * 3;
        int rows = tileH * 3;
        var samples = new float[cols * rows];
        Array.Fill(samples, noData);

        for (int by = 0; by < 3; by++)
        {
            for (int bx = 0; bx < 3; bx++)
            {
                DemRaster? tile = block[bx, by];
                if (tile is null)
                {
                    continue;
                }

                int colOffset = bx * tileW;
                int rowOffset = by * tileH;
                float[] src = tile.Samples;
                for (int r = 0; r < tileH; r++)
                {
                    Array.Copy(src, r * tileW, samples, (((rowOffset + r) * cols) + colOffset), tileW);
                }
            }
        }

        // The window's geographic bounds span the NW corner of the top-left cell to the SE corner of the
        // bottom-right cell of the 3×3 block, anchored on the centre tile's known per-cell pitch. These bounds are
        // only used to satisfy the DemRaster frame; BakeWithMargin re-stamps the cropped core with its EXACT
        // slippy bounds before any base-backfill, so a fractional drift here never reaches the output.
        DemRaster centre = block[1, 1]!;
        double lonPerCol = (centre.East - centre.West) / (tileW - 1);
        double latPerRow = (centre.North - centre.South) / (tileH - 1);
        double west = centre.West - (tileW * lonPerCol);
        double east = centre.East + (tileW * lonPerCol);
        double north = centre.North + (tileH * latPerRow);
        double south = centre.South - (tileH * latPerRow);
        var bounds = new MapBounds(new GeoPoint(south, west), new GeoPoint(north, east));

        return new DemRaster(cols, rows, bounds, samples, noData);
    }

    // Builds the coarse tile at coarseKey by laying the step×step block of finer tiles into a full-footprint
    // NoData raster (so missing finer tiles read as holes, not a shifted mosaic), then averaging it down by
    // step. Returns null when no finer tile in the block exists. The coarse tile carries its true slippy bounds.
    private static BakedDemTile? BuildCoarseTile(
        DemTileKey coarseKey, Dictionary<DemTileKey, BakedDemTile> finerLevel, int step)
    {
        int finerZoom = coarseKey.Zoom + FinerZoomDeltaFor(step);
        int baseX = coarseKey.X * step;
        int baseY = coarseKey.Y * step;

        // Find the uniform finer-tile cell size from any present member of this block.
        int finerCols = 0;
        int finerRows = 0;
        double noData = 0.0;
        bool any = false;
        for (int ty = 0; ty < step && !any; ty++)
        {
            for (int tx = 0; tx < step && !any; tx++)
            {
                if (finerLevel.TryGetValue(new DemTileKey(finerZoom, baseX + tx, baseY + ty), out BakedDemTile? probe))
                {
                    finerCols = probe.Columns;
                    finerRows = probe.Rows;
                    noData = probe.NoDataValue;
                    any = true;
                }
            }
        }

        if (!any)
        {
            return null;
        }

        int blockCols = finerCols * step;
        int blockRows = finerRows * step;
        var blockHeights = new float[blockCols * blockRows];
        Array.Fill(blockHeights, (float)noData);

        for (int ty = 0; ty < step; ty++)
        {
            for (int tx = 0; tx < step; tx++)
            {
                if (!finerLevel.TryGetValue(new DemTileKey(finerZoom, baseX + tx, baseY + ty), out BakedDemTile? finer))
                {
                    continue;
                }

                int colOffset = tx * finerCols;
                int rowOffset = ty * finerRows;
                float[] src = finer.Heights;
                for (int r = 0; r < finer.Rows; r++)
                {
                    Array.Copy(src, r * finer.Columns, blockHeights, ((rowOffset + r) * blockCols) + colOffset, finer.Columns);
                }
            }
        }

        var (west, south, east, north) = SlippyTileMath.TileBounds(coarseKey.X, coarseKey.Y, coarseKey.Zoom);
        var coarseBounds = new MapBounds(new GeoPoint(south, west), new GeoPoint(north, east));
        var block = new BakedDemTile(
            coarseKey.Zoom, coarseKey.X, coarseKey.Y, blockCols, blockRows, coarseBounds, noData, blockHeights);

        return BakedDemDownsampler.Downsample(block, step);
    }

    // step = 2^(finerZoom - coarseZoom) ⇒ finerZoom - coarseZoom = log2(step).
    private static int FinerZoomDeltaFor(int step) => (int)Math.Log2(step);

    private static long WriteTile(string outputDir, BakedDemTile tile)
    {
        string path = Path.Combine(outputDir, BakedDemTileStore.RelativePathFor(tile.Key));
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using (FileStream fs = File.Create(path))
        {
            BakedDemTileStore.Write(fs, tile);
        }

        return new FileInfo(path).Length;
    }
}