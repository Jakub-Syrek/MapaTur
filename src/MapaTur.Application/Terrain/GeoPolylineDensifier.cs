using MapaTur.Domain.Geography;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Inserts intermediate vertices into a geographic polyline so no segment is longer than a given
/// spacing. Overlay lines (trails / roads / route) come from OSM with sparse nodes — long straight
/// segments between them cut across the 1 m detail terrain, so the line floats over dips and the detail
/// mesh occludes it over bulges (looks "toporne" on the fine ground). Densifying first, then seating
/// every vertex on the terrain, makes the line hug the relief.
/// </summary>
public static class GeoPolylineDensifier
{
    /// <summary>
    /// Returns <paramref name="points"/> with extra vertices added along any segment longer than
    /// <paramref name="maxSpacingMeters"/>, by straight lon/lat interpolation. Original vertices are kept,
    /// in order; a polyline with fewer than two points is returned unchanged.
    /// </summary>
    public static IReadOnlyList<GeoPoint> Densify(IReadOnlyList<GeoPoint> points, double maxSpacingMeters)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (maxSpacingMeters <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSpacingMeters), maxSpacingMeters, "Spacing must be positive.");
        }

        if (points.Count < 2)
        {
            return points;
        }

        var result = new List<GeoPoint>(points.Count) { points[0] };
        for (int i = 0; i < points.Count - 1; i++)
        {
            GeoPoint a = points[i];
            GeoPoint b = points[i + 1];
            double distance = a.HaversineDistanceMetersTo(b);
            int subdivisions = Math.Max(1, (int)Math.Ceiling(distance / maxSpacingMeters));

            // Interior points (s = 1 .. subdivisions-1) by interpolation, then the segment's own endpoint b
            // added EXACTLY (not interpolated) — keeps the original OSM nodes intact and float-error-free, and
            // b is added once so the next segment continues from it.
            for (int s = 1; s < subdivisions; s++)
            {
                double t = (double)s / subdivisions;
                result.Add(new GeoPoint(
                    a.Latitude + ((b.Latitude - a.Latitude) * t),
                    a.Longitude + ((b.Longitude - a.Longitude) * t)));
            }

            result.Add(b);
        }

        return result;
    }
}