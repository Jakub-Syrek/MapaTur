namespace MapaTur.Application.Terrain;

/// <summary>
/// Ring-LOD plan for the whole-range STATIC base: splits the base raster into ~<c>tileSideCells</c> plan
/// tiles and assigns each a subsample step from its distance to the focus (the LOD entry look-at) — native
/// step in the near ring, coarser rings further out. The streamed 1 m detail window sits near the focus, so
/// the base grid its boundary meets is as fine as the source allows; against a uniformly coarse base the
/// base's blunted/shifted ridge pokes out past the window edge as a "duplicated ridge" (silhouette jump).
/// Distance is to the tile's NEAREST owned cell, so any tile touching a finer ring is promoted whole.
/// Pure geometry — feed the result to the crack-free <see cref="TerrainMesh3D.BuildAdaptiveTiles"/>
/// (absolute-grid sampling + edge welding), which the per-tile detail already proved on device.
/// </summary>
public static class RingBasePlanner
{
    /// <summary>
    /// Plans the ring-LOD tiling of a <paramref name="columns"/>×<paramref name="rows"/> base raster.
    /// </summary>
    /// <param name="columns">Base raster width, in cells.</param>
    /// <param name="rows">Base raster height, in cells.</param>
    /// <param name="focusColumn">Focus cell column (may lie outside the raster).</param>
    /// <param name="focusRow">Focus cell row (may lie outside the raster).</param>
    /// <param name="cellSizeMeters">Ground size of one cell, in metres.</param>
    /// <param name="nearRadiusMeters">Tiles touching this radius render at <paramref name="nearStep"/>.</param>
    /// <param name="midRadiusMeters">Tiles touching (near, mid] render at <paramref name="midStep"/>; beyond → <paramref name="farStep"/>.</param>
    /// <param name="tileSideCells">Approximate plan-tile side, in cells.</param>
    /// <param name="nearStep">Subsample step inside the near ring (1 = native).</param>
    /// <param name="midStep">Subsample step in the mid ring.</param>
    /// <param name="farStep">Subsample step beyond the mid ring.</param>
    /// <param name="forcedColumnCuts">Extra tile boundaries (cell columns) every tile column must respect —
    /// the ortho CELL boundaries. A tile straddling an ortho cell picks one cell by its centre and clamps the
    /// far-side UV, stretching that cell's edge texels into relief-independent "strata" stripes (the exact
    /// bug BuildTiles fixes with BuildTileCuts). Cuts that would leave a sliver under 2 cells are dropped.</param>
    /// <param name="forcedRowCuts">As <paramref name="forcedColumnCuts"/>, for cell rows.</param>
    public static IReadOnlyList<PerTileLodDecision> Plan(
        int columns,
        int rows,
        int focusColumn,
        int focusRow,
        double cellSizeMeters,
        double nearRadiusMeters,
        double midRadiusMeters,
        int tileSideCells = 250,
        int nearStep = 1,
        int midStep = 2,
        int farStep = 4,
        IEnumerable<int>? forcedColumnCuts = null,
        IEnumerable<int>? forcedRowCuts = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(tileSideCells, 2);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cellSizeMeters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nearRadiusMeters);
        if (midRadiusMeters <= nearRadiusMeters)
        {
            throw new ArgumentException("The rings must be strictly nested: midRadius > nearRadius.", nameof(midRadiusMeters));
        }

        int[] colBounds = Boundaries(columns, tileSideCells, forcedColumnCuts);
        int[] rowBounds = Boundaries(rows, tileSideCells, forcedRowCuts);
        int nCols = colBounds.Length - 1;
        int nRows = rowBounds.Length - 1;

        // Pass 1: each tile's own step from the nearest-owned-cell distance to the focus.
        var steps = new int[nCols * nRows];
        for (int tr = 0; tr < nRows; tr++)
        {
            for (int tc = 0; tc < nCols; tc++)
            {
                double dCols = AxisDistance(focusColumn, colBounds[tc], colBounds[tc + 1] - 1);
                double dRows = AxisDistance(focusRow, rowBounds[tr], rowBounds[tr + 1] - 1);
                double distanceMeters = Math.Sqrt((dCols * dCols) + (dRows * dRows)) * cellSizeMeters;
                steps[(tr * nCols) + tc] = distanceMeters <= nearRadiusMeters ? nearStep
                    : distanceMeters <= midRadiusMeters ? midStep
                    : farStep;
            }
        }

        // Pass 2: decisions with each edge carrying the grid neighbour's step (0 = raster edge), so
        // BuildAdaptiveTiles can weld a finer tile's shared edge to the coarser neighbour's anchors.
        var decisions = new List<PerTileLodDecision>(steps.Length);
        for (int tr = 0; tr < nRows; tr++)
        {
            for (int tc = 0; tc < nCols; tc++)
            {
                decisions.Add(new PerTileLodDecision(
                    colBounds[tc],
                    rowBounds[tr],
                    colBounds[tc + 1] - colBounds[tc],
                    rowBounds[tr + 1] - rowBounds[tr],
                    steps[(tr * nCols) + tc],
                    EdgeStepNorth: tr > 0 ? steps[((tr - 1) * nCols) + tc] : 0,
                    EdgeStepSouth: tr < nRows - 1 ? steps[((tr + 1) * nCols) + tc] : 0,
                    EdgeStepWest: tc > 0 ? steps[(tr * nCols) + (tc - 1)] : 0,
                    EdgeStepEast: tc < nCols - 1 ? steps[(tr * nCols) + (tc + 1)] : 0));
            }
        }

        return decisions;
    }

    // Distance (in cells) from the focus coordinate to the nearest owned cell of [first..last]; 0 inside.
    private static double AxisDistance(int focus, int first, int last) =>
        focus < first ? first - focus : focus > last ? focus - last : 0;

    // Splits [0, total) into near-equal segments of roughly tileSide cells, merged with the forced cuts.
    // A final sweep drops any cut (regular or forced) that would leave a segment under 2 cells, so every
    // tile stays a legal mesh raster.
    private static int[] Boundaries(int total, int tileSide, IEnumerable<int>? forcedCuts)
    {
        int segments = Math.Clamp((int)Math.Round((double)total / tileSide), 1, Math.Max(1, total / 2));
        var cuts = new SortedSet<int>();
        for (int i = 1; i < segments; i++)
        {
            cuts.Add((int)((long)i * total / segments));
        }

        if (forcedCuts is not null)
        {
            foreach (int cut in forcedCuts)
            {
                if (cut > 0 && cut < total)
                {
                    cuts.Add(cut);
                }
            }
        }

        var bounds = new List<int>(cuts.Count + 2) { 0 };
        foreach (int cut in cuts)
        {
            if (cut - bounds[^1] >= 2 && total - cut >= 2)
            {
                bounds.Add(cut);
            }
        }

        bounds.Add(total);
        return bounds.ToArray();
    }
}