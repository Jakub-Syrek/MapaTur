using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Terrain-roughness metric for the screen-space-error LOD (Model 1): how geometrically SHARP a tile is,
/// so detail can be boosted on ridges / walls / gullies and not on smooth ground. Roughness here is LOCAL
/// curvature — how far each cell sits above/below the mean of its neighbours — NOT deviation from a plane,
/// so a smooth (deep) valley reads low while a jagged ridge reads high. The aggregate is a high percentile,
/// not the max, so a single noisy speckle does not masquerade as roughness. No-data cells are ignored.
/// </summary>
public static class DemRasterRoughness
{
    /// <summary>
    /// Roughness in metres = the <paramref name="highPercentile"/> percentile of per-cell local curvature
    /// (<c>|z − mean(valid orthogonal neighbours)|</c>). Flat / planar tiles return ~0; a gentle valley
    /// returns low (uniform small curvature); a ridge or wall returns high; an isolated speckle is dropped
    /// by the percentile. Cells with fewer than two valid neighbours, and no-data cells, are skipped.
    /// </summary>
    /// <param name="raster">Source DEM tile.</param>
    /// <param name="highPercentile">Percentile in [0,1] used to aggregate (0.95 ⇒ ignore the roughest ~5%,
    /// which is where lone speckles live, while a real ridge spans far more than 5% of the tile).</param>
    /// <param name="stride">Sample every <paramref name="stride"/>-th cell as a curvature centre (≥ 1). The
    /// neighbours stay at the native ±1 cell, so this samples FEWER centres without changing the metric scale
    /// (cost ÷ stride²) — unlike subsampling the raster, which would space the neighbours apart and inflate a
    /// smooth slope/valley into false roughness. 1 (default) scans every cell.</param>
    /// <param name="neighborDistance">How many cells away the orthogonal neighbours sit (≥ 1). 1 measures
    /// curvature at the native cell pitch; a larger value measures it over a wider baseline (ridge scale) — on
    /// a fine raster (~1 m) the 1-cell curvature is tiny, so this is the knob that makes ridge roughness
    /// register. A planar slope still reads 0 at any distance (only curvature counts, never gradient).</param>
    public static double Roughness(DemRaster raster, double highPercentile = 0.95, int stride = 1, int neighborDistance = 1)
    {
        ArgumentNullException.ThrowIfNull(raster);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(neighborDistance, 1);

        int cols = raster.Columns;
        int rows = raster.Rows;
        float noData = raster.NoDataValue;

        bool Valid(float v) => !v.Equals(noData) && !float.IsNaN(v);

        var curvatures = new List<double>((cols / stride + 1) * (rows / stride + 1));
        for (int r = 0; r < rows; r += stride)
        {
            for (int c = 0; c < cols; c += stride)
            {
                float z = raster[c, r];
                if (!Valid(z))
                {
                    continue;
                }

                double sum = 0.0;
                int n = 0;
                AccumulateNeighbour(raster, Valid, c - neighborDistance, r, cols, rows, ref sum, ref n);
                AccumulateNeighbour(raster, Valid, c + neighborDistance, r, cols, rows, ref sum, ref n);
                AccumulateNeighbour(raster, Valid, c, r - neighborDistance, cols, rows, ref sum, ref n);
                AccumulateNeighbour(raster, Valid, c, r + neighborDistance, cols, rows, ref sum, ref n);

                if (n < 4)
                {
                    continue; // need all four neighbours — an asymmetric edge cell reads a plain slope as curved
                }

                curvatures.Add(Math.Abs(z - (sum / n)));
            }
        }

        if (curvatures.Count == 0)
        {
            return 0.0;
        }

        curvatures.Sort();
        int index = (int)Math.Round(highPercentile * (curvatures.Count - 1), MidpointRounding.AwayFromZero);
        index = Math.Clamp(index, 0, curvatures.Count - 1);
        return curvatures[index];
    }

    private static void AccumulateNeighbour(
        DemRaster raster, Func<float, bool> valid, int c, int r, int cols, int rows, ref double sum, ref int count)
    {
        if (c < 0 || c >= cols || r < 0 || r >= rows)
        {
            return;
        }

        float v = raster[c, r];
        if (valid(v))
        {
            sum += v;
            count++;
        }
    }
}