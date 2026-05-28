using MapaTur.Domain.Geography;

namespace MapaTur.Domain.Trails;

/// <summary>
/// Douglas–Peucker polyline simplifier for trail geometries. Reduces vertex count
/// while keeping the polyline's shape within an epsilon tolerance, measured as the
/// perpendicular distance (in metres) from each pruned vertex to the chord
/// connecting its kept neighbours.
/// </summary>
/// <remarks>
/// The cross-track distance is computed on a local equirectangular projection
/// centred on the chord midpoint — accurate enough for trail-scale (sub-kilometre)
/// epsilons used by mapping clients, much cheaper than great-circle math.
/// </remarks>
public static class TrailGeometrySimplifier
{
    private const double MetersPerLatDegree = 111_320.0;

    /// <summary>
    /// Returns a simplified copy of <paramref name="geometry"/>, preserving the first
    /// and last vertices and dropping interior vertices whose perpendicular distance
    /// to their containing chord is at most <paramref name="epsilonMeters"/>.
    /// </summary>
    /// <param name="geometry">Polyline vertices.</param>
    /// <param name="epsilonMeters">Tolerance in metres. Must be non-negative. Use 0 to keep every vertex.</param>
    public static IReadOnlyList<GeoPoint> Simplify(IReadOnlyList<GeoPoint> geometry, double epsilonMeters)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        if (epsilonMeters < 0.0 || !double.IsFinite(epsilonMeters))
        {
            throw new ArgumentOutOfRangeException(nameof(epsilonMeters), epsilonMeters, "Epsilon must be a finite non-negative value.");
        }

        if (geometry.Count <= 2)
        {
            return geometry.ToArray();
        }

        if (epsilonMeters == 0.0)
        {
            return geometry.ToArray();
        }

        var keep = new bool[geometry.Count];
        keep[0] = true;
        keep[^1] = true;
        SimplifyRange(geometry, 0, geometry.Count - 1, epsilonMeters, keep);

        var result = new List<GeoPoint>(geometry.Count);
        for (int i = 0; i < geometry.Count; i++)
        {
            if (keep[i])
            {
                result.Add(geometry[i]);
            }
        }
        return result;
    }

    // Iterative Douglas–Peucker via an explicit stack — avoids worst-case
    // recursion depth on long polylines without changing the canonical algorithm.
    private static void SimplifyRange(IReadOnlyList<GeoPoint> geometry, int startIndex, int endIndex, double epsilonMeters, bool[] keep)
    {
        var stack = new Stack<(int Start, int End)>();
        stack.Push((startIndex, endIndex));

        while (stack.Count > 0)
        {
            var (start, end) = stack.Pop();
            if (end <= start + 1)
            {
                continue;
            }

            GeoPoint a = geometry[start];
            GeoPoint b = geometry[end];

            double maxDistance = 0.0;
            int maxIndex = -1;
            for (int i = start + 1; i < end; i++)
            {
                double distance = PerpendicularDistanceMeters(geometry[i], a, b);
                if (distance > maxDistance)
                {
                    maxDistance = distance;
                    maxIndex = i;
                }
            }

            if (maxIndex >= 0 && maxDistance > epsilonMeters)
            {
                keep[maxIndex] = true;
                stack.Push((start, maxIndex));
                stack.Push((maxIndex, end));
            }
        }
    }

    /// <summary>
    /// Perpendicular distance from <paramref name="point"/> to the line segment
    /// <paramref name="lineStart"/>—<paramref name="lineEnd"/>, in metres.
    /// </summary>
    private static double PerpendicularDistanceMeters(GeoPoint point, GeoPoint lineStart, GeoPoint lineEnd)
    {
        // Local equirectangular: scale longitude by cos(refLatitude) so 1° lat ≈ 1° lon
        // in metres. This is accurate within ~0.1% for spans <50 km, which is well above
        // any trail epsilon we'd ever pick.
        double refLat = (lineStart.Latitude + lineEnd.Latitude) * 0.5;
        double metersPerLonDegree = MetersPerLatDegree * Math.Cos(refLat * Math.PI / 180.0);

        double ax = (lineStart.Longitude - point.Longitude) * metersPerLonDegree;
        double ay = (lineStart.Latitude - point.Latitude) * MetersPerLatDegree;
        double bx = (lineEnd.Longitude - point.Longitude) * metersPerLonDegree;
        double by = (lineEnd.Latitude - point.Latitude) * MetersPerLatDegree;

        double dx = bx - ax;
        double dy = by - ay;
        double segmentLengthSquared = (dx * dx) + (dy * dy);
        if (segmentLengthSquared <= 0.0)
        {
            // Degenerate segment — distance is from point to either endpoint.
            return Math.Sqrt((ax * ax) + (ay * ay));
        }

        // Cross-product magnitude / segment length = perpendicular distance.
        double cross = (ax * by) - (ay * bx);
        return Math.Abs(cross) / Math.Sqrt(segmentLengthSquared);
    }
}
