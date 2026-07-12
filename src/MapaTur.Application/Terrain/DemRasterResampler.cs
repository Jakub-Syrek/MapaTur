using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Smooth (Catmull-Rom bicubic) point sampling of a <see cref="DemRaster"/>, sharing
/// <see cref="DemRaster.SampleBilinear"/>'s node registration (node 0 on the west/north edge, node N−1 on
/// the east/south edge) and clamping. Two consumers: the z17 probe (Faza 0 of `docs/PLAN-sub-1m-geometry.md`
/// — the RMS(z17 − upsample(z16)) metric must not read bilinear faceting as "information gained", and
/// Catmull-Rom's linear precision means a planar slope contributes exactly 0 residual) and later the Faza B
/// synthetic-tile upsampler (a parent tile inflated to a child grid without the mid-cell creases bilinear
/// would mesh into visible facets).
///
/// NoData handling: Catmull-Rom weights can be NEGATIVE, so a sentinel tap cannot be dropped-and-renormalised
/// the way the bilinear blend does (a negative weight on a -32768 tap would swing the surface by kilometres).
/// Any sentinel inside the 4×4 footprint therefore degrades that sample to
/// <see cref="DemRaster.SampleBilinear"/>, which is NoData-aware; an all-NoData footprint surfaces
/// <see cref="DemRaster.NoDataValue"/> through the same path.
/// </summary>
public static class DemRasterResampler
{
    /// <summary>
    /// Samples the raster at a WGS-84 point with separable Catmull-Rom over the 4×4 neighbouring nodes
    /// (edge-clamped). Exact at nodes and on planes; falls back to the NoData-aware bilinear blend when any
    /// footprint tap is NoData.
    /// </summary>
    /// <param name="raster">The raster to sample.</param>
    /// <param name="longitude">WGS-84 longitude (clamped to the raster's extent).</param>
    /// <param name="latitude">WGS-84 latitude (clamped to the raster's extent).</param>
    /// <returns>The interpolated elevation, or <see cref="DemRaster.NoDataValue"/> when nothing valid covers
    /// the point.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="raster"/> is null.</exception>
    public static double SampleCatmullRom(DemRaster raster, double longitude, double latitude)
    {
        ArgumentNullException.ThrowIfNull(raster);

        int cols = raster.Columns;
        int rows = raster.Rows;

        double rowFloat = (raster.North - latitude) / (raster.North - raster.South) * (rows - 1);
        double colFloat = (longitude - raster.West) / (raster.East - raster.West) * (cols - 1);
        rowFloat = Math.Clamp(rowFloat, 0.0, rows - 1.0);
        colFloat = Math.Clamp(colFloat, 0.0, cols - 1.0);

        int r1 = Math.Min((int)Math.Floor(rowFloat), rows - 1);
        int c1 = Math.Min((int)Math.Floor(colFloat), cols - 1);
        double tr = rowFloat - r1;
        double tc = colFloat - c1;

        float noData = raster.NoDataValue;
        Span<double> rowValues = stackalloc double[4];
        Span<double> taps = stackalloc double[4];
        for (int i = 0; i < 4; i++)
        {
            int r = Math.Clamp(r1 - 1 + i, 0, rows - 1);
            for (int j = 0; j < 4; j++)
            {
                int c = Math.Clamp(c1 - 1 + j, 0, cols - 1);
                float v = raster[c, r];
                if (v == noData)
                {
                    // A sentinel under a (possibly negative) cubic weight would poison the surface — degrade
                    // this sample to the NoData-aware bilinear blend instead.
                    return raster.SampleBilinear(longitude, latitude);
                }

                taps[j] = v;
            }

            rowValues[i] = CatmullRom(taps[0], taps[1], taps[2], taps[3], tc);
        }

        return CatmullRom(rowValues[0], rowValues[1], rowValues[2], rowValues[3], tr);
    }

    // Standard centripetal-free (uniform) Catmull-Rom: interpolates p1..p2 for t in [0,1], passing through
    // the control points with C1 continuity and exact linear precision.
    private static double CatmullRom(double p0, double p1, double p2, double p3, double t)
    {
        double t2 = t * t;
        double t3 = t2 * t;
        return 0.5 * ((2.0 * p1)
            + ((p2 - p0) * t)
            + (((2.0 * p0) - (5.0 * p1) + (4.0 * p2) - p3) * t2)
            + (((3.0 * p1) - p0 - (3.0 * p2) + p3) * t3));
    }
}