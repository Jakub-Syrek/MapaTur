using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Extracts iso-elevation contour lines from a <see cref="DemRaster"/> via marching squares: each grid
/// cell is classified against a level and the crossing points on its four edges are joined into segments.
/// Output is geographic (lon/lat) line segments per level — ready to densify and drape on the 3D relief,
/// so they read as a flat topographic map looking straight down and flow into the terrain as it is tilted.
/// </summary>
public static class ContourGenerator
{
    /// <summary>
    /// Generates contour segments for every level in <paramref name="levels"/> across the whole raster.
    /// A cell touching a no-data corner is skipped (no contour is invented over missing ground), and a
    /// cell is only tested against levels that fall within its own min/max corner range.
    /// </summary>
    public static IReadOnlyList<ContourSegment> Generate(DemRaster raster, IReadOnlyList<double> levels)
    {
        ArgumentNullException.ThrowIfNull(raster);
        ArgumentNullException.ThrowIfNull(levels);

        var segments = new List<ContourSegment>();
        double noData = raster.NoDataValue;

        double LonAt(int c) => raster.West + ((double)c / (raster.Columns - 1) * (raster.East - raster.West));
        double LatAt(int r) => raster.North - ((double)r / (raster.Rows - 1) * (raster.North - raster.South));

        for (int r = 0; r < raster.Rows - 1; r++)
        {
            double latTop = LatAt(r);
            double latBottom = LatAt(r + 1);
            for (int c = 0; c < raster.Columns - 1; c++)
            {
                double tl = raster[c, r];
                double tr = raster[c + 1, r];
                double br = raster[c + 1, r + 1];
                double bl = raster[c, r + 1];

                if (tl == noData || tr == noData || br == noData || bl == noData)
                {
                    continue;
                }

                double cellMin = Math.Min(Math.Min(tl, tr), Math.Min(br, bl));
                double cellMax = Math.Max(Math.Max(tl, tr), Math.Max(br, bl));

                double lonLeft = LonAt(c);
                double lonRight = LonAt(c + 1);
                var tlPos = new GeoPoint(latTop, lonLeft);
                var trPos = new GeoPoint(latTop, lonRight);
                var brPos = new GeoPoint(latBottom, lonRight);
                var blPos = new GeoPoint(latBottom, lonLeft);

                foreach (double level in levels)
                {
                    if (level < cellMin || level > cellMax)
                    {
                        continue;
                    }

                    AppendCellSegments(segments, level, tl, tr, br, bl, tlPos, trPos, brPos, blPos);
                }
            }
        }

        return segments;
    }

    // One cell. Corner bits ABOVE the level: TL=8, TR=4, BR=2, BL=1. Each crossed edge's point is the
    // linear interpolation of the level along that edge; the case selects which edge points to join.
    private static void AppendCellSegments(
        List<ContourSegment> segments, double level,
        double tl, double tr, double br, double bl,
        GeoPoint tlPos, GeoPoint trPos, GeoPoint brPos, GeoPoint blPos)
    {
        int code = (tl > level ? 8 : 0) | (tr > level ? 4 : 0) | (br > level ? 2 : 0) | (bl > level ? 1 : 0);
        if (code == 0 || code == 15)
        {
            return; // the cell is wholly above or below the level — no crossing
        }

        GeoPoint top = Lerp(tlPos, trPos, Frac(tl, tr, level));
        GeoPoint right = Lerp(trPos, brPos, Frac(tr, br, level));
        GeoPoint bottom = Lerp(brPos, blPos, Frac(br, bl, level));
        GeoPoint left = Lerp(blPos, tlPos, Frac(bl, tl, level));

        switch (code)
        {
            case 1: case 14: Add(segments, level, left, bottom); break;
            case 2: case 13: Add(segments, level, bottom, right); break;
            case 3: case 12: Add(segments, level, left, right); break;
            case 4: case 11: Add(segments, level, top, right); break;
            case 6: case 9: Add(segments, level, top, bottom); break;
            case 7: case 8: Add(segments, level, top, left); break;
            case 5: // saddle (TR + BL above): wrap each above-corner — top↔right and bottom↔left
                Add(segments, level, top, right);
                Add(segments, level, bottom, left);
                break;
            case 10: // saddle (TL + BR above): top↔left and bottom↔right
                Add(segments, level, top, left);
                Add(segments, level, bottom, right);
                break;
        }
    }

    private static double Frac(double a, double b, double level)
    {
        double d = b - a;
        double t = d == 0.0 ? 0.5 : (level - a) / d;
        // A straddling edge always yields t in [0,1]; an UNUSED (non-straddling) edge — computed unconditionally
        // above — can extrapolate far past its endpoints (huge when the two corners are nearly equal). Clamp so
        // every crossing stays ON its edge and produces a valid lat/lon (an out-of-range GeoPoint would throw).
        return Math.Clamp(t, 0.0, 1.0);
    }

    private static GeoPoint Lerp(GeoPoint a, GeoPoint b, double t) =>
        new(a.Latitude + (t * (b.Latitude - a.Latitude)), a.Longitude + (t * (b.Longitude - a.Longitude)));

    private static void Add(List<ContourSegment> segments, double level, GeoPoint p0, GeoPoint p1) =>
        segments.Add(new ContourSegment(level, p0, p1));
}