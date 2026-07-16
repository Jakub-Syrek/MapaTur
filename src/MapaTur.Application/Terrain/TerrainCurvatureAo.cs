using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Per-vertex ambient-occlusion factor from DEM curvature, baked into the mesh at build time (the light
/// &amp; shadow plan's B). Lambert + hemispheric ambient light every slope by its own normal only — a gully
/// floor with walls rising all around receives exactly the same ambient as an open ridge, which flattens
/// the relief's depth. This probes how much the SURROUNDINGS rise above a vertex on three METRIC radii
/// (fine crevice → couloir → cirque scale) and converts the mean horizon angle into a darkening factor:
/// 1 = fully open (flat ground, ridges, convex ground — AO never brightens), down to <see cref="MinAo"/>
/// on deeply enclosed floors (never black: the sky ambient floor must stay readable). Pure math, no GL —
/// the shader just multiplies the light sum by the baked value.
/// </summary>
public static class TerrainCurvatureAo
{
    /// <summary>Darkest allowed factor — a fully enclosed floor keeps 40% of its ambient.</summary>
    public const float MinAo = 0.4f;

    /// <summary>
    /// The widest probe ring in metres. Public because anyone meshing a SUB-raster (a baked tile with a
    /// neighbour halo) must supply at least this much real surrounding data, or the ring clamps at the raster
    /// edge and bakes a tonal band along it (measured on the z17 pyramid: a ±0.08 AO step at every tile border).
    /// </summary>
    public const double MaxProbeRadiusMeters = 45.0;

    // Probe radii in METRES (not cells), so the coarse base and the 1 m tiles shade consistently: a
    // couloir reads equally occluded whether its floor vertex came from a 15 m or a 1 m grid.
    private static readonly double[] RadiiMeters = { 6.0, 18.0, MaxProbeRadiusMeters };

    // 8 ring directions (unit steps); the probe walks radiusCells along each.
    private static readonly (int Dc, int Dr)[] Directions =
    {
        (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1), (0, -1), (1, -1),
    };

    /// <summary>
    /// The occlusion factor at raster cell (<paramref name="col"/>, <paramref name="row"/>) in
    /// [<see cref="MinAo"/>, 1]. <paramref name="cellSizeMeters"/> is the raster's ground cell size —
    /// the metric radii are converted through it. Invalid/NoData/out-of-raster probes count as OPEN
    /// (occlusion is never invented from missing data).
    /// </summary>
    public static float At(DemRaster raster, int col, int row, double cellSizeMeters)
    {
        ArgumentNullException.ThrowIfNull(raster);
        if (cellSizeMeters <= 0)
        {
            return 1f;
        }

        float h = raster[col, row];
        if (h == raster.NoDataValue)
        {
            return 1f;
        }

        double occlusionSum = 0;
        foreach (double radiusMeters in RadiiMeters)
        {
            int radiusCells = Math.Max(1, (int)Math.Round(radiusMeters / cellSizeMeters));
            double effectiveRadius = radiusCells * cellSizeMeters;

            double rise = 0;
            int valid = 0;
            foreach ((int dc, int dr) in Directions)
            {
                int c = col + (dc * radiusCells);
                int r = row + (dr * radiusCells);
                if (c < 0 || r < 0 || c >= raster.Columns || r >= raster.Rows)
                {
                    continue;
                }

                float sample = raster[c, r];
                if (sample == raster.NoDataValue)
                {
                    continue;
                }

                rise += Math.Max(0, sample - h); // only ground ABOVE the vertex occludes; below = open sky
                valid++;
            }

            if (valid < 3)
            {
                continue; // ring mostly off-raster/NoData — contribute nothing rather than guess
            }

            // Mean horizon angle of the ring, normalised so a 45° rim = full occlusion for its scale.
            double meanRise = rise / valid;
            occlusionSum += Math.Clamp(Math.Atan2(meanRise, effectiveRadius) / (Math.PI / 4.0), 0.0, 1.0);
        }

        double occlusion = occlusionSum / RadiiMeters.Length; // rings that contributed nothing count as open
        return (float)Math.Clamp(1.0 - ((1.0 - MinAo) * occlusion), MinAo, 1.0);
    }
}