using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Repairs a z16 detail window tile-by-tile with a per-tile cache, instead of running the neighbour-heavy
/// repairs (FillNarrowZeroStrips + FillPits) over the WHOLE stitched window on every camera move. Each tile is
/// repaired from its own region PLUS a <c>margin</c> of neighbour cells (so edge-of-tile pits/strips keep their
/// context), then the repaired core is cached by slippy (X,Y). With a margin ≥ the repairs' reach the assembled
/// result is identical to repairing the whole window (verified by tests), so the only change is cost: a sliding
/// window repairs just the NEW tiles. A tile whose full margin is NOT inside the loaded window (a window-edge
/// tile) is repaired fresh and NOT cached, so a cached tile is always one that had complete context.
/// </summary>
public static class PerTileRepair
{
    /// <summary>Returns <paramref name="rawWindow"/> with FillNarrowZeroStrips + FillPits applied, reusing cached
    /// per-tile repairs for unchanged ground so a sliding window only repairs newly-entered tiles.</summary>
    /// <param name="rawWindow">The stitched, UNrepaired z16 window (whole-tile aligned).</param>
    /// <param name="tiles">The slippy tiles that make up <paramref name="rawWindow"/>.</param>
    /// <param name="minTileX">Smallest tile X in <paramref name="tiles"/> (column 0 of the window).</param>
    /// <param name="minTileY">Smallest tile Y in <paramref name="tiles"/> (row 0 of the window).</param>
    /// <param name="tilePxX">Tile width in cells.</param>
    /// <param name="tilePxY">Tile height in cells.</param>
    /// <param name="cache">Per-tile repaired-raster cache (round-scoped).</param>
    /// <param name="margin">Neighbour cells included around each tile before repair (≥ the repairs' reach).</param>
    /// <param name="pitDepthMeters">FillPits threshold.</param>
    /// <param name="zeroStripMaxCells">FillNarrowZeroStrips max strip width.</param>
    public static DemRaster RepairWindowCached(
        DemRaster rawWindow,
        IReadOnlyList<DemTileKey> tiles,
        int minTileX,
        int minTileY,
        int tilePxX,
        int tilePxY,
        PerTileRepairCache cache,
        int margin,
        double pitDepthMeters,
        int zeroStripMaxCells)
    {
        ArgumentNullException.ThrowIfNull(rawWindow);
        ArgumentNullException.ThrowIfNull(tiles);
        ArgumentNullException.ThrowIfNull(cache);

        int cols = rawWindow.Columns;
        int rows = rawWindow.Rows;
        // Rent the working buffer (becomes the detail raster, ~90 MB) from the pool instead of cloning a fresh array
        // every reload; the VM returns the old detail raster after a 2-build delay (the render reads it async). Copy
        // raw in (RentSingle returns a possibly-dirty recycled buffer), then overwrite each tile's repaired core.
        int n = rawWindow.Samples.Length;
        float[] outSamples = MeshBufferPool.Shared.RentSingle(n);
        Array.Copy(rawWindow.Samples, outSamples, n);

        foreach (DemTileKey t in tiles)
        {
            int lc0 = (t.X - minTileX) * tilePxX;
            int lr0 = (t.Y - minTileY) * tilePxY;
            int coreW = Math.Min(tilePxX, cols - lc0);
            int coreH = Math.Min(tilePxY, rows - lr0);
            if (coreW <= 0 || coreH <= 0)
            {
                continue;
            }

            DemRaster RepairCore()
            {
                int mc0 = Math.Max(0, lc0 - margin);
                int mr0 = Math.Max(0, lr0 - margin);
                int mc1 = Math.Min(cols, lc0 + coreW + margin);
                int mr1 = Math.Min(rows, lr0 + coreH + margin);
                DemRaster sub = rawWindow.Crop(mc0, mr0, mc1 - mc0, mr1 - mr0);
                sub = DemRasterRepair.FillNarrowZeroStrips(sub, zeroStripMaxCells);
                sub = DemRasterRepair.FillPits(sub, pitDepthMeters);
                return sub.Crop(lc0 - mc0, lr0 - mr0, coreW, coreH);
            }

            // Cache only tiles whose full margin sits INSIDE the window — those had complete neighbour context, so
            // the cached core is valid wherever the tile reappears. Window-edge tiles (clamped margin) repair fresh.
            bool fullMargin = lc0 - margin >= 0 && lr0 - margin >= 0
                && lc0 + coreW + margin <= cols && lr0 + coreH + margin <= rows;
            DemRaster core = fullMargin ? cache.GetOrBuild((t.X, t.Y), RepairCore) : RepairCore();

            for (int r = 0; r < coreH; r++)
            {
                Array.Copy(core.Samples, r * core.Columns, outSamples, ((lr0 + r) * cols) + lc0, coreW);
            }
        }

        return new DemRaster(cols, rows, rawWindow.Bounds, outSamples, rawWindow.NoDataValue);
    }
}