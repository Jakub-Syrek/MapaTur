using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Trails;

namespace MapaTur.Application.Tests.Terrain;

public sealed class RouteTrailConflationTests
{
    // ~1 m in latitude degrees near 49°N; longitude metres-per-degree shrink by cos(lat).
    private const double MetreLat = 1.0 / 111_320.0;
    private static double MetreLon(double lat) => 1.0 / (111_320.0 * Math.Cos(lat * Math.PI / 180.0));

    private static Trail Trail(params GeoPoint[] geometry) =>
        new(1L, "t", new List<TrailMarking> { new(PttkColor.Red) }, geometry.ToList());

    [Fact]
    public void Conflate_NullRoute_Throws()
    {
        Action act = () => RouteTrailConflation.Conflate(null!, Array.Empty<Trail>());

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Conflate_NullTrails_Throws()
    {
        Action act = () => RouteTrailConflation.Conflate(Array.Empty<GeoPoint>(), null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Conflate_ShortRoute_ReturnedUnchanged()
    {
        var route = new[] { new GeoPoint(49.0, 19.0) };

        RouteTrailConflation.Conflate(route, Array.Empty<Trail>()).Should().BeSameAs(route);
    }

    [Fact]
    public void Conflate_SnappedRoute_SnapsBackOntoTrailVertices()
    {
        // Trail runs due north; the route is the same edge but shifted ~4 m east (inside the snap tolerance).
        var v0 = new GeoPoint(49.0, 19.0);
        var v1 = new GeoPoint(49.0 + (11 * MetreLat), 19.0);
        var trail = Trail(v0, v1);

        double east4 = 4 * MetreLon(49.0);
        var route = new[] { new GeoPoint(v0.Latitude, v0.Longitude + east4), new GeoPoint(v1.Latitude, v1.Longitude + east4) };

        var result = RouteTrailConflation.Conflate(route, new[] { trail });

        result.Should().HaveCount(2);
        result[0].Longitude.Should().BeApproximately(v0.Longitude, 1e-9);
        result[0].Latitude.Should().BeApproximately(v0.Latitude, 1e-9);
        result[1].Longitude.Should().BeApproximately(v1.Longitude, 1e-9);
        result[1].Latitude.Should().BeApproximately(v1.Latitude, 1e-9);
    }

    [Fact]
    public void Conflate_RouteInsideRenderedTrailSegment_ProjectsEveryPointOntoThatSegment()
    {
        // The rendered 3D trail is simplified to one straight segment, while the planner still carries the
        // denser route points a few metres beside it. Rendering must follow the visible trail segment, not keep
        // the planner's small side arc.
        var v0 = new GeoPoint(49.0, 19.0);
        var v1 = new GeoPoint(49.0 + (100 * MetreLat), 19.0);
        var trail = Trail(v0, v1);

        double east4 = 4 * MetreLon(49.0);
        var route = new[]
        {
            new GeoPoint(v0.Latitude, v0.Longitude + east4),
            new GeoPoint(49.0 + (50 * MetreLat), v0.Longitude + east4),
            new GeoPoint(v1.Latitude, v1.Longitude + east4),
        };

        var result = RouteTrailConflation.Conflate(route, new[] { trail });

        result.Should().HaveCount(3);
        result.Should().OnlyContain(p => Math.Abs(p.Longitude - v0.Longitude) < 1e-9);
        result[0].Latitude.Should().BeApproximately(v0.Latitude, 1e-9);
        result[1].Latitude.Should().BeApproximately(route[1].Latitude, 1e-9);
        result[2].Latitude.Should().BeApproximately(v1.Latitude, 1e-9);
    }

    [Fact]
    public void Conflate_RouteInsideSimplificationTolerance_StillFollowsRenderedTrailSegment()
    {
        // 3D trails are simplified for rendering. A detailed route can be more than the graph snap tolerance
        // from the simplified segment and still visibly belong to that rendered trail.
        var v0 = new GeoPoint(49.0, 19.0);
        var v1 = new GeoPoint(49.0 + (100 * MetreLat), 19.0);
        var trail = Trail(v0, v1);

        double east15 = 15 * MetreLon(49.0);
        var route = new[]
        {
            new GeoPoint(v0.Latitude, v0.Longitude + east15),
            new GeoPoint(v1.Latitude, v1.Longitude + east15),
        };

        var result = RouteTrailConflation.Conflate(route, new[] { trail });

        result[0].Longitude.Should().BeApproximately(v0.Longitude, 1e-9);
        result[1].Longitude.Should().BeApproximately(v1.Longitude, 1e-9);
    }

    [Fact]
    public void Conflate_NoTrailWithinTolerance_KeepsOriginalGeometry()
    {
        var v0 = new GeoPoint(49.0, 19.0);
        var v1 = new GeoPoint(49.0 + (11 * MetreLat), 19.0);
        var trail = Trail(v0, v1);

        // Route 50 m east — far outside the 6 m tolerance.
        double east50 = 50 * MetreLon(49.0);
        var route = new[] { new GeoPoint(v0.Latitude, v0.Longitude + east50), new GeoPoint(v1.Latitude, v1.Longitude + east50) };

        var result = RouteTrailConflation.Conflate(route, new[] { trail });

        result[0].Longitude.Should().BeApproximately(route[0].Longitude, 1e-12);
        result[1].Longitude.Should().BeApproximately(route[1].Longitude, 1e-12);
    }

    [Fact]
    public void Conflate_TrailStoredReversed_StillMatchesInRouteDirection()
    {
        var v0 = new GeoPoint(49.0, 19.0);
        var v1 = new GeoPoint(49.0 + (11 * MetreLat), 19.0);
        var trail = Trail(v1, v0); // stored reversed

        double east4 = 4 * MetreLon(49.0);
        var route = new[] { new GeoPoint(v0.Latitude, v0.Longitude + east4), new GeoPoint(v1.Latitude, v1.Longitude + east4) };

        var result = RouteTrailConflation.Conflate(route, new[] { trail });

        result[0].Latitude.Should().BeApproximately(v0.Latitude, 1e-9); // follows route direction (v0 → v1)
        result[1].Latitude.Should().BeApproximately(v1.Latitude, 1e-9);
    }

    [Fact]
    public void Conflate_ParallelDuplicate_NeverGrabsTheWrongCopy()
    {
        // The route runs on trail A (4 m off); trail B is a parallel copy 20 m east. The conflation must follow A.
        var a0 = new GeoPoint(49.0, 19.0);
        var a1 = new GeoPoint(49.0 + (11 * MetreLat), 19.0);
        double east20 = 20 * MetreLon(49.0);
        var b0 = new GeoPoint(49.0, 19.0 + east20);
        var b1 = new GeoPoint(49.0 + (11 * MetreLat), 19.0 + east20);

        double east4 = 4 * MetreLon(49.0);
        var route = new[] { new GeoPoint(a0.Latitude, a0.Longitude + east4), new GeoPoint(a1.Latitude, a1.Longitude + east4) };

        var result = RouteTrailConflation.Conflate(route, new[] { Trail(a0, a1), new Trail(2L, "B", new List<TrailMarking> { new(PttkColor.Red) }, new List<GeoPoint> { b0, b1 }) });

        result[0].Longitude.Should().BeApproximately(a0.Longitude, 1e-9); // A, not B
        result[1].Longitude.Should().BeApproximately(a1.Longitude, 1e-9);
    }
}