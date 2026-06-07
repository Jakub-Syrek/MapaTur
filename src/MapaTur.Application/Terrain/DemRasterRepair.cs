using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Repairs gaps in a <see cref="DemRaster"/> before it reaches the mesh builder, which has no NoData
/// handling (a NoData sample would otherwise become a vertex at the sentinel depth — a spike/streak).
/// <see cref="FillNoData"/> replaces every NoData cell with the nearest valid elevation along its row,
/// then its column, so a region whose bbox clips a coverage edge (e.g. the Slovak border for a GUGiK
/// Tatra patch) extends flat to the edge instead of plunging.
/// </summary>
public static class DemRasterRepair
{
    /// <summary>
    /// Returns a copy of <paramref name="raster"/> with NoData cells filled from the nearest valid
    /// neighbour (row pass, then column pass). A raster with no valid samples is returned unchanged.
    /// </summary>
    public static DemRaster FillNoData(DemRaster raster)
    {
        ArgumentNullException.ThrowIfNull(raster);

        float noData = raster.NoDataValue;
        int cols = raster.Columns;
        int rows = raster.Rows;
        float[] s = (float[])raster.Samples.Clone();

        bool IsHole(float v) => v.Equals(noData) || float.IsNaN(v);

        // Row pass: forward then backward fill within each row.
        for (int r = 0; r < rows; r++)
        {
            int rowBase = r * cols;
            FillRun(s, rowBase, cols, stride: 1, IsHole);
            FillRun(s, rowBase + ((cols - 1) * 1), cols, stride: -1, IsHole);
        }

        // Column pass: fills cells the row pass couldn't (whole-NoData rows), top-down then bottom-up.
        for (int c = 0; c < cols; c++)
        {
            FillRun(s, c, rows, stride: cols, IsHole);
            FillRun(s, c + ((rows - 1) * cols), rows, stride: -cols, IsHole);
        }

        return new DemRaster(cols, rows, raster.Bounds, s, noData);
    }

    /// <summary>
    /// Returns a copy with every valid cell strictly below <paramref name="floorMeters"/> set to NoData, so
    /// the NoData-aware mesh holes them through to the base. GUGiK returns a flat ~0 OUTSIDE its coverage
    /// (instead of a NoData sentinel), which would otherwise render as a flat plate below the terrain; for a
    /// mountain patch (real terrain well above the floor) this drops only those coverage-edge artefacts.
    /// Note: a Tatra-context guard — a per-cell floor would hole genuine lowland if used nationwide.
    /// </summary>
    public static DemRaster HoleBelow(DemRaster raster, double floorMeters)
    {
        ArgumentNullException.ThrowIfNull(raster);

        float noData = raster.NoDataValue;
        float[] s = (float[])raster.Samples.Clone();
        for (int i = 0; i < s.Length; i++)
        {
            if (!s[i].Equals(noData) && s[i] < floorMeters)
            {
                s[i] = noData;
            }
        }

        return new DemRaster(raster.Columns, raster.Rows, raster.Bounds, s, noData);
    }

    // Walks `count` samples from `start` by `stride`, carrying the last valid value forward into holes.
    private static void FillRun(float[] s, int start, int count, int stride, Func<float, bool> isHole)
    {
        float last = 0f;
        bool haveLast = false;
        int i = start;
        for (int n = 0; n < count; n++, i += stride)
        {
            if (!isHole(s[i]))
            {
                last = s[i];
                haveLast = true;
            }
            else if (haveLast)
            {
                s[i] = last;
            }
        }
    }
}