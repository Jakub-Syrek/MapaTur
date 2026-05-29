using FluentAssertions;

using MapaTur.Domain.Geography;
using MapaTur.Domain.Routing;
using MapaTur.Domain.Trails;
using MapaTur.Routing.Costs;
using MapaTur.Routing.Graph;

namespace MapaTur.Routing.Tests;

public sealed class AStarRouterTests
{
    [Fact]
    public void FindPath_ReturnsNullWhenStartEqualsGoal()
    {
        var graph = TrailGraph.Build([MakeTrail(1,
            new GeoPoint(49.0, 19.0),
            new GeoPoint(49.001, 19.001))]);

        var router = new AStarRouter(graph);
        var route = router.FindPath(new NodeId(0), new NodeId(0), new DistanceCostFunction());

        route.Should().BeNull();
    }

    [Fact]
    public void FindPath_ReturnsNullWhenDisconnected()
    {
        var trail1 = MakeTrail(1, new GeoPoint(49.0, 19.0), new GeoPoint(49.001, 19.001));
        var trail2 = MakeTrail(2, new GeoPoint(50.0, 20.0), new GeoPoint(50.001, 20.001));
        var graph = TrailGraph.Build([trail1, trail2]);

        var router = new AStarRouter(graph);
        var route = router.FindPath(new NodeId(0), new NodeId(2), new DistanceCostFunction());

        route.Should().BeNull();
    }

    [Fact]
    public void FindPath_PicksShortestRouteOnATriangle()
    {
        // Build a triangle: A-B (short), A-C-B (longer via detour).
        var pointA = new GeoPoint(49.0000, 19.0000);
        var pointB = new GeoPoint(49.0050, 19.0050);
        var pointC = new GeoPoint(49.0100, 19.0000); // detour

        var directTrail = MakeTrail(1, pointA, pointB);
        var detourTrail = MakeTrail(2, pointA, pointC, pointB);

        var graph = TrailGraph.Build([directTrail, detourTrail]);
        var router = new AStarRouter(graph);

        var startNode = graph.FindNearestNode(pointA);
        var goalNode = graph.FindNearestNode(pointB);

        var route = router.FindPath(startNode, goalNode, new DistanceCostFunction());

        route.Should().NotBeNull();
        // Direct route is one segment; detour route would be two.
        route!.Segments.Should().HaveCount(1);
    }

    [Fact]
    public void FindPath_DistanceCostMinimisesTotalDistance()
    {
        var pointA = new GeoPoint(49.0000, 19.0000);
        var pointB = new GeoPoint(49.0030, 19.0030);
        var pointMiddle = new GeoPoint(49.0015, 19.0015);

        // Two-hop path via middle should win against any other configuration here.
        var trail = MakeTrail(1, pointA, pointMiddle, pointB);
        var graph = TrailGraph.Build([trail]);
        var router = new AStarRouter(graph);

        var route = router.FindPath(
            graph.FindNearestNode(pointA),
            graph.FindNearestNode(pointB),
            new DistanceCostFunction());

        route.Should().NotBeNull();
        route!.Segments.Should().HaveCount(2);
        route.TotalDistanceMeters.Should().BeGreaterThan(0.0);
    }

    [Fact]
    public void FindPath_TimeCostProducesNonZeroDuration()
    {
        var trail = MakeTrail(1,
            new GeoPoint(49.0, 19.0, elevationMeters: 1000),
            new GeoPoint(49.001, 19.001, elevationMeters: 1010),
            new GeoPoint(49.002, 19.002, elevationMeters: 1020));

        var graph = TrailGraph.Build([trail]);
        var router = new AStarRouter(graph);
        var route = router.FindPath(new NodeId(0), new NodeId(2), new TimeCostFunction());

        route.Should().NotBeNull();
        route!.TotalDurationSeconds.Should().BeGreaterThan(0.0);
        route.TotalAscentMeters.Should().BeApproximately(20.0, 1e-3);
    }

    private static Trail MakeTrail(long id, params GeoPoint[] points)
    {
        return new Trail(id, $"trail-{id}", [new TrailMarking(PttkColor.Red)], points);
    }
}