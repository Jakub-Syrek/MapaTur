using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Terrain-roughness metrics for the screen-space-error LOD (Model 1): grade detail by how much terrain a
/// tile actually contains, not just by distance. The geometric error of a tile is how far its true surface
/// strays from the coarsest representation — a single corner-bilinear quad — so a jagged ridge demands HD
/// even kilometres away while a planar slope stays coarse.
/// </summary>
public static class DemRasterRoughness
{
    /// <summary>
    /// Max vertical distance (metres) between the raster's true surface and the bilinear surface through its
    /// four corners — the geometric error of representing the whole tile as one quad. Flat or planar tiles
    /// return ~0; rugged tiles return a large value. No-data cells are skipped; a no-data corner (a coverage
    /// edge with no fittable quad) yields 0.
    /// </summary>
    public static double MaxDeviationFromBilinear(DemRaster raster)
    {
        ArgumentNullException.ThrowIfNull(raster);

        int cols = raster.Columns;
        int rows = raster.Rows;
        float noData = raster.NoDataValue;

        double nw = raster[0, 0];
        double ne = raster[cols - 1, 0];
        double sw = raster[0, rows - 1];
        double se = raster[cols - 1, rows - 1];

        if (nw == noData || ne == noData || sw == noData || se == noData)
        {
            return 0.0;
        }

        double maxDeviation = 0.0;
        for (int r = 0; r < rows; r++)
        {
            double fr = rows > 1 ? (double)r / (rows - 1) : 0.0;
            for (int c = 0; c < cols; c++)
            {
                double actual = raster[c, r];
                if (actual == noData)
                {
                    continue;
                }

                double fc = cols > 1 ? (double)c / (cols - 1) : 0.0;
                double top = nw + ((ne - nw) * fc);
                double bottom = sw + ((se - sw) * fc);
                double bilinear = top + ((bottom - top) * fr);

                double deviation = Math.Abs(actual - bilinear);
                if (deviation > maxDeviation)
                {
                    maxDeviation = deviation;
                }
            }
        }

        return maxDeviation;
    }
}