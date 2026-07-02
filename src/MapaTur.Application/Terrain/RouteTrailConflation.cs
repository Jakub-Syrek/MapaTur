using MapaTur.Domain.Geography;
using MapaTur.Domain.Trails;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Re-lays a planned route's polyline onto the ACTUAL trail it traverses, so the rendered route lies on its trail
/// instead of the routing graph's snapped node coordinates (which sit up to the graph snap tolerance — and, where
/// OSM duplicates a path as a relation + a way, up to ~20 m — off the drawn trail; the rendered trail is also
/// SIMPLIFIED for display, so a denser route can drift a few metres beside the visible segment).
/// <para>
/// For each route point we find the NEAREST trail SEGMENT (perpendicular/clamped distance) within
/// <c>toleranceMeters</c> and output the PROJECTION of the point onto that segment; if no trail is within tolerance
/// the original point is kept (a genuine off-trail connector). The route point COUNT is preserved. Because the match
/// is nearest-segment-wins, a parallel duplicate trail farther away can't be grabbed, and a trail stored in either
/// direction matches the same — the projection only depends on the segment line, not its vertex order. It does not
/// touch the planner: the chosen path is unchanged; only the rendered geometry is pulled onto the real OSM trail.
/// </para>
/// <para>
/// Trail segments are indexed in a coarse lat/lon spatial hash so each route point only tests the handful of
/// segments near it — O(route + trail) instead of O(route × trail), so a long route over a dense trail set
/// doesn't stall the render thread (this runs on the render thread when the route changes).
/// </para>
/// </summary>
public static class RouteTrailConflation
{
    private const double MetresPerDegreeLat = 111_320.0;

    /// <summary>
    /// Returns the route polyline re-laid onto the trail it follows, ONE output point per input point. A point with
    /// no trail segment within <paramref name="toleranceMeters"/> keeps its original coordinates.
    /// </summary>
    /// <param name="route">Planned route polyline (lon/lat).</param>
    /// <param name="trails">Rendered trails to snap onto (their segments are projected onto).</param>
    /// <param name="toleranceMeters">Max perpendicular distance a route point may sit from a trail segment and still
    /// be projected onto it. Default 18 m: comfortably covers the graph snap tolerance plus the display
    /// simplification drift, while staying well under the ~20 m gap to a parallel OSM duplicate (nearest-wins keeps
    /// the route on the trail it actually runs along).</param>
    public static IReadOnlyList<GeoPoint> Conflate(
        IReadOnlyList<GeoPoint> route,
        IReadOnlyList<Trail> trails,
        double toleranceMeters = 18.0)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(trails);
        if (route.Count < 2)
        {
            return route;
        }

        var index = new SegmentIndex(toleranceMeters);
        foreach (Trail trail in trails)
        {
            IReadOnlyList<GeoPoint> geo = trail.Geometry;
            for (var i = 0; i + 1 < geo.Count; i++)
            {
                index.Add(geo[i], geo[i + 1]);
            }
        }

        var output = new GeoPoint[route.Count];
        for (var i = 0; i < route.Count; i++)
        {
            GeoPoint p = route[i];
            output[i] = index.TryProject(p, toleranceMeters, out GeoPoint projected) ? projected : p;
        }

        return output;
    }

    // Spatial hash of trail segments keyed by the cells their bounding box spans, so a route point only tests the
    // segments in its own cell + 8 neighbours. Each query returns the projection of the point onto the nearest
    // segment within tolerance (perpendicular, clamped to the segment endpoints).
    private sealed class SegmentIndex
    {
        private readonly double cellSizeDeg;
        private readonly Dictionary<(long, long), List<(GeoPoint A, GeoPoint B)>> cells = new();

        public SegmentIndex(double toleranceMeters)
        {
            // Cell side = 2× tolerance (in latitude metres); the 3×3 neighbour scan then always covers a
            // tolerance-radius match. Longitude cells are slightly wider E-W than tolerance — still covered.
            cellSizeDeg = Math.Max(1e-7, (toleranceMeters * 2.0) / MetresPerDegreeLat);
        }

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

        // Finds the nearest segment to p within tolerance and returns the projection of p onto it. Distance and the
        // projection are computed in a local equirectangular frame about p (X east, Y north, metres), so the result
        // is direction-agnostic (a trail stored reversed projects identically).
        public bool TryProject(GeoPoint p, double tolerance, out GeoPoint projected)
        {
            projected = default;
            double bestDistSq = tolerance * tolerance;
            var found = false;

            double cosLat = Math.Cos(p.Latitude * Math.PI / 180.0);
            double metresPerDegLon = MetresPerDegreeLat * cosLat;

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
                        double ax = (a.Longitude - p.Longitude) * metresPerDegLon;
                        double ay = (a.Latitude - p.Latitude) * MetresPerDegreeLat;
                        double bx = (b.Longitude - p.Longitude) * metresPerDegLon;
                        double by = (b.Latitude - p.Latitude) * MetresPerDegreeLat;

                        double abx = bx - ax, aby = by - ay;
                        double lenSq = (abx * abx) + (aby * aby);
                        double t = lenSq <= 1e-9 ? 0.0 : Math.Clamp(((-ax * abx) + (-ay * aby)) / lenSq, 0.0, 1.0);
                        double projX = ax + (abx * t);
                        double projY = ay + (aby * t);

                        double distSq = (projX * projX) + (projY * projY);
                        if (distSq < bestDistSq)
                        {
                            bestDistSq = distSq;
                            // Convert the local-frame projection back to lon/lat about p.
                            double lon = p.Longitude + (metresPerDegLon > 1e-9 ? projX / metresPerDegLon : 0.0);
                            double lat = p.Latitude + (projY / MetresPerDegreeLat);
                            projected = new GeoPoint(lat, lon);
                            found = true;
                        }
                    }
                }
            }

            return found;
        }

        private (long, long) Cell(GeoPoint p) =>
            ((long)Math.Floor(p.Longitude / cellSizeDeg), (long)Math.Floor(p.Latitude / cellSizeDeg));
    }
}