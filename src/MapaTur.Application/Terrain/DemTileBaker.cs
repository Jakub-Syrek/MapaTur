using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Bakes a raw z16 DEM tile into a repaired <see cref="BakedDemTile"/> by running the EXACT per-tile repair
/// chain the live detail path runs (<c>MapPageViewModel.BuildPerTileDetailAsync</c>):
/// <see cref="DemRasterRepair.FillNarrowZeroStrips"/>(24) → <see cref="DemRasterRepair.FillPits"/>(20) →
/// <see cref="DemRasterRepair.HoleBelow"/>(<see cref="DefaultCoverageFloorMeters"/>) →
/// <see cref="DemRasterRepair.FillNoDataFrom"/>(coarse base). Doing it offline once means the runtime can
/// stream the finished heightmap instead of repairing on every camera move.
///
/// <para><b>Seam-safe (no walls/cracks).</b> Two effects would otherwise put a vertical wall at every tile
/// boundary, and <see cref="BakeWithMargin"/> fixes both. (1) The neighbour-aware passes (FillNarrowZeroStrips,
/// FillPits) run over a margin-expanded crop — the tile PLUS a ring of neighbour cells ≥ the repairs' reach,
/// mirroring <see cref="PerTileRepair.RepairWindowCached"/> — so the repair itself never makes a tile's edge
/// diverge from its neighbour. (2) Adjacent slippy tiles SHARE their boundary meridian/parallel but the raw
/// source returns slightly different heights there (each tile's fetch-downsample sees a different
/// neighbourhood); so the boundary cells are WELDED to the mean of the geographically-coincident neighbour
/// cells in the margin, which both adjacent tiles compute identically ⇒ the shared edge is bit-identical. The
/// per-cell passes (HoleBelow, FillNoDataFrom) have no neighbour reach and sample the base at the shared point,
/// so they preserve the weld.</para>
/// </summary>
public static class DemTileBaker
{
    /// <summary>Widest flat-zero strip (in cells) the bake bridges — mirrors the live detail path.</summary>
    public const int DefaultZeroStripMaxCells = 24;

    /// <summary>Pit depth (metres below the neighbour reference) the bake despikes — mirrors the live detail path.</summary>
    public const double DefaultPitDepthMeters = 20.0;

    /// <summary>Elevation (metres) below which a valid cell is GUGiK out-of-coverage flat-0 and is holed
    /// through to the base — mirrors <c>MapPageViewModel.DetailCoverageFloorMeters</c>.</summary>
    public const double DefaultCoverageFloorMeters = 100.0;

    /// <summary>
    /// Repairs a SINGLE tile (no neighbour margin) and returns it as a <see cref="BakedDemTile"/> addressed by
    /// <paramref name="key"/>. Runs the full live chain; pass <paramref name="baseDem"/> to backfill NoData
    /// voids (out-of-coverage holes punched by <see cref="DemRasterRepair.HoleBelow"/>, plus watercourse/border
    /// voids) from the coarse base so the mesh shows base-height terrain there instead of see-through gaps.
    /// <para>Because the neighbour-aware passes run on the tile alone here, the result is NOT guaranteed to
    /// agree with a neighbour on a shared edge — use <see cref="BakeWithMargin"/> for the region pyramid. This
    /// overload exists for callers that already hold the full tile context (verification / tests).</para>
    /// </summary>
    /// <param name="rawTile">The raw (un-repaired) DEM raster for the tile, as fetched/decoded from source.</param>
    /// <param name="key">The slippy address to stamp on the baked tile.</param>
    /// <param name="baseDem">Coarse base DEM (e.g. <c>tatry.dem</c>) for void backfill; null skips that pass.</param>
    /// <param name="zeroStripMaxCells">FillNarrowZeroStrips max strip width (defaults to the live value).</param>
    /// <param name="pitDepthMeters">FillPits depth threshold (defaults to the live value).</param>
    /// <param name="coverageFloorMeters">HoleBelow floor (defaults to the live value).</param>
    /// <returns>The repaired, addressed baked tile.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rawTile"/> is null.</exception>
    public static BakedDemTile Bake(
        DemRaster rawTile,
        DemTileKey key,
        DemRaster? baseDem = null,
        int zeroStripMaxCells = DefaultZeroStripMaxCells,
        double pitDepthMeters = DefaultPitDepthMeters,
        double coverageFloorMeters = DefaultCoverageFloorMeters)
    {
        ArgumentNullException.ThrowIfNull(rawTile);

        DemRaster repaired = RepairFullChain(rawTile, baseDem, zeroStripMaxCells, pitDepthMeters, coverageFloorMeters);
        return new BakedDemTile(
            key.Zoom, key.X, key.Y, repaired.Columns, repaired.Rows, repaired.Bounds, repaired.NoDataValue,
            repaired.Samples);
    }

    /// <summary>
    /// Bakes the tile whose footprint is the core rectangle
    /// [<paramref name="coreCol"/>, <paramref name="coreCol"/>+<paramref name="coreWidth"/>) ×
    /// [<paramref name="coreRow"/>, <paramref name="coreRow"/>+<paramref name="coreHeight"/>) inside the
    /// margin-expanded <paramref name="window"/> (the tile stitched together with its neighbours). The
    /// neighbour-aware repairs run over the WHOLE window so the tile's edge cells carry real neighbour context,
    /// then the repaired core is cropped out — making adjacent tiles' shared edges bit-identical (the crack/wall
    /// fix). The cropped core's geographic bounds equal the tile's true slippy bounds (carried by
    /// <see cref="DemRaster.Crop"/>), so the baked tile lines up exactly with the rest of the scene.
    /// </summary>
    /// <param name="window">The stitched raw window (the tile + a neighbour margin), whole-tile aligned.</param>
    /// <param name="coreCol">Column offset of the tile's west edge within <paramref name="window"/>.</param>
    /// <param name="coreRow">Row offset of the tile's north edge within <paramref name="window"/>.</param>
    /// <param name="coreWidth">Tile width in cells.</param>
    /// <param name="coreHeight">Tile height in cells.</param>
    /// <param name="key">The slippy address to stamp on the baked tile.</param>
    /// <param name="baseDem">Coarse base DEM for void backfill; null skips that pass.</param>
    /// <param name="zeroStripMaxCells">FillNarrowZeroStrips max strip width (defaults to the live value).</param>
    /// <param name="pitDepthMeters">FillPits depth threshold (defaults to the live value).</param>
    /// <param name="coverageFloorMeters">HoleBelow floor (defaults to the live value).</param>
    /// <param name="dealiasCellSizeMeters">&gt; 0 applies <see cref="DemRasterDealias"/> (wariant 3: global
    /// de-alias + slope-gated wall smooth) on the margin window before the weld, using this REGION-WIDE
    /// constant ground cell pitch — deterministic across neighbouring windows, which the bit-identical seam
    /// weld requires. 0 (default) skips the filter.</param>
    /// <returns>The repaired tile cropped to its footprint, addressed by <paramref name="key"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="window"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The core rectangle does not lie within the window.</exception>
    public static BakedDemTile BakeWithMargin(
        DemRaster window,
        int coreCol,
        int coreRow,
        int coreWidth,
        int coreHeight,
        DemTileKey key,
        DemRaster? baseDem = null,
        int zeroStripMaxCells = DefaultZeroStripMaxCells,
        double pitDepthMeters = DefaultPitDepthMeters,
        double coverageFloorMeters = DefaultCoverageFloorMeters,
        double dealiasCellSizeMeters = 0.0)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (coreCol < 0 || coreRow < 0 || coreWidth < 2 || coreHeight < 2
            || coreCol + coreWidth > window.Columns || coreRow + coreHeight > window.Rows)
        {
            throw new ArgumentOutOfRangeException(
                nameof(coreCol), "The core tile rectangle must lie within the window and be at least 2×2.");
        }

        // Neighbour-aware passes on the WHOLE window (so the core's edges see real neighbours), then crop the
        // core out. With a margin ≥ the repairs' reach the cropped core equals what a whole-window repair would
        // have produced, so the repairs themselves don't make a tile's edge diverge from its neighbour
        // (PerTileRepair's invariant).
        DemRaster bridged = DemRasterRepair.FillNarrowZeroStrips(window, zeroStripMaxCells);
        DemRaster despiked = DemRasterRepair.FillPits(bridged, pitDepthMeters);

        // Wariant 3 (z17): de-alias + slope-gated wall smooth on the WHOLE margin window (kernel radius
        // ≤ 5 cells ≪ margin), so adjacent tiles filter identically in their overlap and the weld below
        // still lands on bit-identical shared cells — which REQUIRES the explicit region-wide cell size
        // (a window-bounds-derived scale differs between neighbours by ulps and broke the seam verify).
        // Runs BEFORE HoleBelow — the filter itself excludes sub-floor flat-0 and NoData cells.
        if (dealiasCellSizeMeters > 0.0)
        {
            despiked = DemRasterDealias.Apply(despiked, coverageFloorMeters, dealiasCellSizeMeters);
        }

        // SEAM WELD. Adjacent slippy tiles SHARE their boundary meridian/parallel (a 256-sample tile has column 0
        // on its west edge and column 255 on its east edge = the next tile's west edge — the SAME ground point),
        // yet the raw source returns slightly DIFFERENT heights there (each tile's Gaussian fetch-downsample sees a
        // different neighbourhood), so meshing tiles independently puts two different-height vertices at one XY = a
        // vertical wall at every seam. Reconcile each core boundary cell to the MEAN of the (≤4) window cells that
        // are geographically coincident with it (the cell itself + its across-the-boundary neighbour(s); 2 on an
        // edge, 4 at a corner). Both neighbours sit inside the margin and within the repairs' reach, so the
        // adjacent tile computes the SAME mean for the same shared cell ⇒ the welded edges are bit-identical (no
        // wall). Read from the post-repair snapshot so writes never perturb a later cell's inputs.
        float[] core = WeldCoreEdges(despiked, coreCol, coreRow, coreWidth, coreHeight);

        // Re-stamp the core with its TRUE slippy bounds (the window's frame is approximate; an interpolated crop
        // bound could drift a fraction of a cell, which would mis-register the base-backfill SampleBilinear).
        var (west, south, east, north) = SlippyTileMath.TileBounds(key.X, key.Y, key.Zoom);
        var coreBounds = new MapBounds(new GeoPoint(south, west), new GeoPoint(north, east));
        var coreRaster = new DemRaster(coreWidth, coreHeight, coreBounds, core, window.NoDataValue);

        // Per-cell passes (no neighbour reach) on the cropped core — HoleBelow then base backfill. The welded
        // boundary value is identical in both tiles and both sample the base at the SAME shared meridian, so these
        // pure passes preserve the seam agreement.
        DemRaster holed = DemRasterRepair.HoleBelow(coreRaster, coverageFloorMeters);
        DemRaster filled = baseDem is not null ? DemRasterRepair.FillNoDataFrom(holed, baseDem) : holed;

        return new BakedDemTile(
            key.Zoom, key.X, key.Y, filled.Columns, filled.Rows, filled.Bounds, filled.NoDataValue,
            filled.Samples);
    }

    // Crops the core [coreCol,coreRow,coreWidth,coreHeight] out of the repaired window, welding each core BOUNDARY
    // cell to the mean of the window cells geographically coincident with it (see BakeWithMargin). Interior cells
    // are copied unchanged. Reads <paramref name="window"/> as an immutable snapshot, so the order of writes never
    // affects a cell's inputs — the result is independent of iteration order (and therefore identical for the
    // neighbour tile that shares the same coincident cells).
    private static float[] WeldCoreEdges(DemRaster window, int coreCol, int coreRow, int coreWidth, int coreHeight)
    {
        int wCols = window.Columns;
        int wRows = window.Rows;
        float noData = window.NoDataValue;
        float[] w = window.Samples;
        var core = new float[coreWidth * coreHeight];

        bool IsValid(float v) => !v.Equals(noData) && !float.IsNaN(v);

        Span<int> cc = stackalloc int[3]; // coincident columns for the current cell (1 interior, 2 edge)
        Span<int> rr = stackalloc int[3]; // coincident rows
        for (int lr = 0; lr < coreHeight; lr++)
        {
            int wr = coreRow + lr;
            for (int lc = 0; lc < coreWidth; lc++)
            {
                int wc = coreCol + lc;
                float self = w[(wr * wCols) + wc];

                bool onWest = lc == 0 && coreCol > 0;
                bool onEast = lc == coreWidth - 1 && wc + 1 < wCols;
                bool onNorth = lr == 0 && coreRow > 0;
                bool onSouth = lr == coreHeight - 1 && wr + 1 < wRows;

                if (!onWest && !onEast && !onNorth && !onSouth)
                {
                    core[(lr * coreWidth) + lc] = self; // interior — untouched
                    continue;
                }

                // Coincident columns = this column plus the across-boundary column on a west/east edge; likewise
                // rows. The cross product gives the cell's coincident set (2 on an edge, 4 at a corner).
                int nc = 0;
                cc[nc++] = wc;
                if (onWest) { cc[nc++] = wc - 1; }
                if (onEast) { cc[nc++] = wc + 1; }

                int nrw = 0;
                rr[nrw++] = wr;
                if (onNorth) { rr[nrw++] = wr - 1; }
                if (onSouth) { rr[nrw++] = wr + 1; }

                double sum = 0.0;
                int count = 0;
                for (int i = 0; i < nrw; i++)
                {
                    int rowBase = rr[i] * wCols;
                    for (int j = 0; j < nc; j++)
                    {
                        float v = w[rowBase + cc[j]];
                        if (IsValid(v))
                        {
                            sum += v;
                            count++;
                        }
                    }
                }

                // Mean of the valid coincident cells; if every coincident cell is NoData (no neighbour tile there),
                // keep the cell's own value so a region-edge tile is unchanged.
                core[(lr * coreWidth) + lc] = count > 0 ? (float)(sum / count) : self;
            }
        }

        return core;
    }

    // The full live per-tile chain on a single raster: FillNarrowZeroStrips → FillPits → HoleBelow →
    // FillNoDataFrom(base). Reuses the real repair functions — no second implementation to drift.
    private static DemRaster RepairFullChain(
        DemRaster raster, DemRaster? baseDem, int zeroStripMaxCells, double pitDepthMeters, double coverageFloorMeters)
    {
        DemRaster bridged = DemRasterRepair.FillNarrowZeroStrips(raster, zeroStripMaxCells);
        DemRaster despiked = DemRasterRepair.FillPits(bridged, pitDepthMeters);
        DemRaster holed = DemRasterRepair.HoleBelow(despiked, coverageFloorMeters);
        return baseDem is not null ? DemRasterRepair.FillNoDataFrom(holed, baseDem) : holed;
    }
}