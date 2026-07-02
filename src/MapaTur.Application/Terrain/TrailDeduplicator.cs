using MapaTur.Domain.Geography;
using MapaTur.Domain.Trails;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Drops near-parallel DUPLICATE trails before they are drawn/rasterised. OSM frequently carries the same physical
/// path twice — once as a <c>route=hiking</c> relation and once as the underlying <c>highway=path</c> way — and the
/// two copies can sit up to ~20 m apart. Rendered, that shows as a trail running beside the route (and the route's
/// conflation can snap to whichever copy the planner didn't take). This is a render-side cleanup ONLY: it never
/// touches the routing graph or path selection, only which of two coincident polylines is drawn.
/// <para>
/// CONSERVATIVE by design: a trail is dropped only when it runs within a small lateral distance of an already-kept
/// trail for MOST of its length (a high fraction of its sampled points). A trail that merely brushes another
/// (a crossing, a shared trailhead, a short shared stretch) is kept — it is a genuinely different path. Of a
/// duplicate pair the LONGER/more-detailed trail is kept (it is processed first), so the survivor is the better copy.
/// </para>
/// <para>
/// Kept-trail segments are indexed in a coarse lat/lon spatial hash so each candidate only tests the handful of
/// segments near it — O(total samples) rather than O(trails²), so a dense network doesn't stall the render thread.
/// </para>
/// </summary>
public static class TrailDeduplicator
{
    private const double MetresPerDegreeLat = 111_320.0;

    /// <summary>Default lateral distance (m) within which a candidate point counts as "on" a kept trail.</summary>
    public const double DefaultLateralToleranceMeters = 10.0;

    /// <summary>Default fraction of a candidate's sampled points that must be within tolerance to drop it.</summary>
    public const double DefaultOverlapFraction = 0.70;

    /// <summary>Spacing (m) candidate polylines are sampled at, so sparse 2-vertex trails are evaluated fairly.</summary>
    private const double SampleSpacingMeters = 10.0;

    /// <summary>
    /// Returns the input trails with near-parallel duplicates removed, preserving the input order of the kept
    /// trails. The longer trail of a duplicate pair is the one kept.
    /// </summary>
    /// <param name="trails">Trails to de-duplicate.</param>
    /// <param name="lateralToleranceMeters">Max lateral distance for a point to count as coincident with a kept trail.</param>
    /// <param name="overlapFraction">Fraction of a candidate's sampled points that must be coincident to drop it.</param>
    public static IReadOnlyList<Trail> Deduplicate(
        IReadOnlyList<Trail> trails,
        double lateralToleranceMeters = DefaultLateralToleranceMeters,
        double overlapFraction = DefaultOverlapFraction)
    {
        ArgumentNullException.ThrowIfNull(trails);
        if (trails.Count < 2)
        {
            return trails;
        }

        // Process longest-first so the survivor of a duplicate pair is the longer/more-detailed copy. Carry the
        // original index so the kept set can be returned in input order (stable, predictable on screen).
        var ordered = new List<(int Index, Trail Trail, double Length)>(trails.Count);
        for (var i = 0; i < trails.Count; i++)
        {
            ordered.Add((i, trails[i], PolylineLengthMetres(trails[i].Geometry)));
        }

        ordered.Sort((x, y) => y.Length.CompareTo(x.Length));

        var index = new SegmentIndex(lateralToleranceMeters);
        var keptIndices = new List<int>();

        foreach ((int originalIndex, Trail trail, _) in ordered)
        {
            if (IsDuplicateOfKept(trail.Geometry, index, lateralToleranceMeters, overlapFraction))
            {
                continue; // near-parallel duplicate of a trail we already kept — drop it.
            }

            keptIndices.Add(originalIndex);
            AddTrailSegments(trail.Geometry, index);
        }

        keptIndices.Sort();
        var result = new List<Trail>(keptIndices.Count);
        foreach (int i in keptIndices)
        {
            result.Add(trails[i]);
        }

        return result;
    }

    // A candidate is a duplicate when a high fraction of its sampled points lie within tolerance of a kept segment.
    // Sampling along the segments (not just at vertices) means a sparse 2-vertex trail is judged on its whole length,
    // so a trail that only crosses a kept one (coincident at a single sample, not most) is correctly KEPT.
    private static bool IsDuplicateOfKept(
        IReadOnlyList<GeoPoint> geometry,
        SegmentIndex index,
        double tolerance,
        double overlapFraction)
    {
        if (index.IsEmpty || geometry.Count < 2)
        {
            return false;
        }

        long total = 0;
        long near = 0;

        for (var i = 0; i + 1 < geometry.Count; i++)
        {
            GeoPoint a = geometry[i];
            GeoPoint b = geometry[i + 1];
            double segLen = PlanarMetres(a, b);
            int steps = Math.Max(1, (int)Math.Ceiling(segLen / SampleSpacingMeters));

            // Sample the segment's start plus interior/end points (skip the start of inner segments to avoid
            // double-counting the shared vertex).
            int startK = i == 0 ? 0 : 1;
            for (int k = startK; k <= steps; k++)
            {
                double t = (double)k / steps;
                GeoPoint p = Lerp(a, b, t);
                total++;
                if (index.HasSegmentWithin(p, tolerance))
                {
                    near++;
                }
            }
        }

        return total > 0 && (double)near / total >= overlapFraction;
    }

    private static void AddTrailSegments(IReadOnlyList<GeoPoint> geometry, SegmentIndex index)
    {
        for (var i = 0; i + 1 < geometry.Count; i++)
        {
            index.Add(geometry[i], geometry[i + 1]);
        }
    }

    private static double PolylineLengthMetres(IReadOnlyList<GeoPoint> geometry)
    {
        double sum = 0;
        for (var i = 0; i + 1 < geometry.Count; i++)
        {
            sum += PlanarMetres(geometry[i], geometry[i + 1]);
        }

        return sum;
    }

    private static GeoPoint Lerp(GeoPoint a, GeoPoint b, double t) =>
        new(a.Latitude + ((b.Latitude - a.Latitude) * t), a.Longitude + ((b.Longitude - a.Longitude) * t));

    // Cheap equirectangular distance — exact enough at the few-metre tolerance and far faster than haversine.
    private static double PlanarMetres(GeoPoint p, GeoPoint q)
    {
        double midLatRad = (p.Latitude + q.Latitude) * 0.5 * Math.PI / 180.0;
        double dx = (p.Longitude - q.Longitude) * MetresPerDegreeLat * Math.Cos(midLatRad);
        double dy = (p.Latitude - q.Latitude) * MetresPerDegreeLat;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    // Spatial hash of kept trail segments keyed by the cells their bounding box spans, so a candidate point only
    // tests segments in its own cell + 8 neighbours. Cell side = 2× tolerance (in latitude metres).
    private sealed class SegmentIndex
    {
        private readonly double tolerance;
        private readonly double cellSizeDeg;
        private readonly Dictionary<(long, long), List<(GeoPoint A, GeoPoint B)>> cells = new();

        public SegmentIndex(double toleranceMeters)
        {
            tolerance = toleranceMeters;
            cellSizeDeg = Math.Max(1e-7, (toleranceMeters * 2.0) / MetresPerDegreeLat);
        }

        public bool IsEmpty => cells.Count == 0;

        public void Add(GeoPoint a, GeoPoint b)
        {
            (long ax, long ay) = Cell(a);
            (long bx, long by) = Cell(b);
            long minX = Math.Min(ax, bx), maxX = Math.Max(ax, bx);
            long minY = Math.Min(ay, by), maxY = Math.Max(ay, by);
            for (long cx = minX; cx <= maxX; cx++)
            {
                for (long cy = minY; cy <= maxY; cy++)
                {
                    if (!cells.TryGetValue((cx, cy), out var bucket))
                    {
                        bucket = new List<(GeoPoint, GeoPoint)>(2);
                        cells[(cx, cy)] = bucket;
                    }

                    bucket.Add((a, b));
                }
            }
        }

        public bool HasSegmentWithin(GeoPoint p, double withinMeters)
        {
            (long cx, long cy) = Cell(p);
            for (long dx = -1; dx <= 1; dx++)
            {
                for (long dy = -1; dy <= 1; dy++)
                {
                    if (!cells.TryGetValue((cx + dx, cy + dy), out var bucket))
                    {
                        continue;
                    }

                    foreach ((GeoPoint a, GeoPoint b) in bucket)
                    {
                        if (DistanceToSegmentMetres(p, a, b) <= withinMeters)
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private (long, long) Cell(GeoPoint p) =>
            ((long)Math.Floor(p.Longitude / cellSizeDeg), (long)Math.Floor(p.Latitude / cellSizeDeg));

        // Point-to-segment distance in metres via a local equirectangular projection about the point.
        private static double DistanceToSegmentMetres(GeoPoint p, GeoPoint a, GeoPoint b)
        {
            double latRad = p.Latitude * Math.PI / 180.0;
            double cosLat = Math.Cos(latRad);
            double px = 0.0, py = 0.0;
            double ax = (a.Longitude - p.Longitude) * MetresPerDegreeLat * cosLat;
            double ay = (a.Latitude - p.Latitude) * MetresPerDegreeLat;
            double bx = (b.Longitude - p.Longitude) * MetresPerDegreeLat * cosLat;
            double by = (b.Latitude - p.Latitude) * MetresPerDegreeLat;

            double abx = bx - ax, aby = by - ay;
            double lenSq = (abx * abx) + (aby * aby);
            if (lenSq <= 1e-9)
            {
                return Math.Sqrt(((px - ax) * (px - ax)) + ((py - ay) * (py - ay)));
            }

            double t = Math.Clamp((((px - ax) * abx) + ((py - ay) * aby)) / lenSq, 0.0, 1.0);
            double projX = ax + (abx * t);
            double projY = ay + (aby * t);
            return Math.Sqrt(((px - projX) * (px - projX)) + ((py - projY) * (py - projY)));
        }
    }
}