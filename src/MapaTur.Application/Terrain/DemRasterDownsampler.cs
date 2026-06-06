using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Decimates a <see cref="DemRaster"/> to a target sample budget. Used to keep work that scales with
/// raster size — peak detection's per-cell dominance-window scan in particular — cheap on dense LiDAR
/// rasters, independently of the (much larger) sample budget the renderer itself can handle. The cost
/// of a ground-relative window scan grows with resolution²·area, so a z16 1 m raster that the GPU shows
/// fine can still take ~10¹² ops to scan; detecting peaks on a coarse copy fixes that without touching
/// the full-resolution mesh.
/// </summary>
public static class DemRasterDownsampler
{
    /// <summary>
    /// Returns <paramref name="raster"/> decimated (nearest-neighbour, via <see cref="DemRaster.Subsample"/>)
    /// so its sample count is at most <paramref name="maxCells"/>. A raster already within budget is
    /// returned unchanged. Bounds and the NoData sentinel are preserved.
    /// </summary>
    /// <param name="raster">Source raster.</param>
    /// <param name="maxCells">Maximum samples the result may have (at least 4, i.e. a 2×2 raster).</param>
    public static DemRaster SubsampleToMaxCells(DemRaster raster, int maxCells)
    {
        ArgumentNullException.ThrowIfNull(raster);
        if (maxCells < 4)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxCells), maxCells, "Budget must allow at least a 2x2 raster.");
        }

        long cells = (long)raster.Columns * raster.Rows;
        if (cells <= maxCells)
        {
            return raster;
        }

        // ceil(sqrt(cells/budget)) is the first candidate; rounding in Subsample's ceil-division can
        // leave it marginally over budget, so nudge the step up until the result actually fits.
        int step = (int)Math.Ceiling(Math.Sqrt((double)cells / maxCells));
        while (step < Math.Max(raster.Columns, raster.Rows) && CellsAfter(raster, step) > maxCells)
        {
            step++;
        }

        return raster.Subsample(step);
    }

    private static long CellsAfter(DemRaster raster, int step) =>
        (long)((raster.Columns + step - 1) / step) * ((raster.Rows + step - 1) / step);
}