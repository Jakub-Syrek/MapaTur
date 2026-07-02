namespace MapaTur.Application.Terrain;

/// <summary>
/// NoData-aware box-average reducer that derives a coarser baked tile from a finer one, building the lower
/// LOD levels of the offline pyramid. Each output cell is the MEAN of its <c>factor</c>×<c>factor</c> block
/// of source cells, counting only the valid (non-NoData) ones; an output cell is NoData only when EVERY
/// source cell feeding it is NoData. Averaging (vs the renderer's nearest-neighbour decimation) keeps a
/// coarse level's elevation an honest mean of the ground beneath it. The geographic <see cref="BakedDemTile.Bounds"/>
/// and the NoData sentinel are preserved; only the cell pitch grows (dimensions are ceil-divided by the factor).
/// Deterministic: identical input yields identical output.
/// </summary>
public static class BakedDemDownsampler
{
    /// <summary>
    /// Returns <paramref name="tile"/> reduced by <paramref name="factor"/> using a NoData-aware box average.
    /// The result covers the same bounds at <c>ceil(Columns/factor)</c> × <c>ceil(Rows/factor)</c> cells and
    /// keeps the source's zoom/address (the caller re-stamps the coarser slippy key when composing the pyramid).
    /// </summary>
    /// <param name="tile">Source baked tile.</param>
    /// <param name="factor">Block size per axis to average (at least 2).</param>
    /// <returns>The coarser baked tile.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tile"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="factor"/> is below 2.</exception>
    public static BakedDemTile Downsample(BakedDemTile tile, int factor)
        => Downsample(tile, factor, out _);

    /// <summary>
    /// As <see cref="Downsample(BakedDemTile, int)"/>, additionally emitting the per-output-cell mid-frequency
    /// DETAIL: <paramref name="detailRms"/>[i] is the RMS (in metres) of each fine cell's deviation from its
    /// block mean — i.e. exactly the sub-cell relief this averaging discards. Computed in the SAME block loop
    /// (a running sum of squares alongside the mean), so it is the honest residual of the very cells being
    /// averaged, not a re-estimate. A block with no relief (flat ground, a lake bench) yields 0; an all-NoData
    /// block yields 0 (never NoData — the renderer must not shade a hole). The result carries it as
    /// <see cref="BakedDemTile.DetailRms"/> so the streamed coarse tile can shade as if it kept those bumps.
    /// </summary>
    /// <param name="tile">Source baked tile.</param>
    /// <param name="factor">Block size per axis to average (at least 2).</param>
    /// <param name="detailRms">Receives the per-coarse-cell residual RMS grid (metres), row-major,
    /// <c>dstCols</c> × <c>dstRows</c> — the same dimensions as the returned tile's heights.</param>
    /// <returns>The coarser baked tile (carrying <paramref name="detailRms"/> as its detail channel).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tile"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="factor"/> is below 2.</exception>
    public static BakedDemTile Downsample(BakedDemTile tile, int factor, out float[] detailRms)
    {
        ArgumentNullException.ThrowIfNull(tile);
        ArgumentOutOfRangeException.ThrowIfLessThan(factor, 2);

        int srcCols = tile.Columns;
        int srcRows = tile.Rows;
        int dstCols = (srcCols + factor - 1) / factor;
        int dstRows = (srcRows + factor - 1) / factor;

        float noData = (float)tile.NoDataValue;
        float[] src = tile.Heights;
        var dst = new float[dstCols * dstRows];
        var detail = new float[dstCols * dstRows];

        for (int dr = 0; dr < dstRows; dr++)
        {
            int srcRow0 = dr * factor;
            int srcRow1 = Math.Min(srcRow0 + factor, srcRows);
            for (int dc = 0; dc < dstCols; dc++)
            {
                int srcCol0 = dc * factor;
                int srcCol1 = Math.Min(srcCol0 + factor, srcCols);

                double sum = 0.0;
                double sumSq = 0.0;
                int valid = 0;
                for (int sr = srcRow0; sr < srcRow1; sr++)
                {
                    int rowBase = sr * srcCols;
                    for (int sc = srcCol0; sc < srcCol1; sc++)
                    {
                        float v = src[rowBase + sc];
                        if (!v.Equals(noData) && !float.IsNaN(v))
                        {
                            sum += v;
                            sumSq += (double)v * v;
                            valid++;
                        }
                    }
                }

                int idx = (dr * dstCols) + dc;
                if (valid > 0)
                {
                    double mean = sum / valid;
                    dst[idx] = (float)mean;
                    // Variance of the valid fine cells about their mean; divide by the ACTUAL valid count (edge
                    // blocks are smaller than factor²). Clamp ≥ 0 against float round-off, sqrt → RMS in metres.
                    double var = Math.Max(0.0, (sumSq / valid) - (mean * mean));
                    detail[idx] = (float)Math.Sqrt(var);
                }
                else
                {
                    dst[idx] = noData;
                    detail[idx] = 0f; // a hole: no relief to restore, and the shader must never bump NoData
                }
            }
        }

        detailRms = detail;
        return new BakedDemTile(
            tile.Zoom, tile.TileX, tile.TileY, dstCols, dstRows, tile.Bounds, tile.NoDataValue, dst, detail);
    }
}