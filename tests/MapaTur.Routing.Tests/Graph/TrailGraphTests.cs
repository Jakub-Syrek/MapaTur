using FluentAssertions;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Trails;
using MapaTur.Routing.Graph;

namespace MapaTur.Routing.Tests.Graph;

public sealed class TrailGraphTests
{
    [Fact]
    public void Build_SnapsNearbyPointsToSingleNode()
    {
        // Two trails sharing one endpoint within 1m. After snapping, the graph should
        // contain exactly 3 unique nodes (start1, junction, end2).
        var trail1 = MakeTrail(1,
            new GeoPoint(49.0000, 19.0000),
            new GeoPoint(49.0010, 19.0010));
        var trail2 = MakeTrail(2,
            new GeoPoint(49.0010, 19.00100005), // ~5 cm away from trail1's endpoint
            new GeoPoint(49.0020, 19.0020));

        var graph = TrailGraph.Build([trail1, trail2], snapToleranceMeters: 5.0);

        graph.NodeCount.Should().Be(3);
    }

    [Fact]
    public void Build_CreatesBidirectionalEdges()
    {
        var trail = MakeTrail(1,
            new GeoPoint(49.0000, 19.0000),
            new GeoPoint(49.0010, 19.0010));

        var graph = TrailGraph.Build([trail]);

        graph.NodeCount.Should().Be(2);
        graph.GetEdges(new Domain.Routing.NodeId(0)).Should().HaveCount(1);
        graph.GetEdges(new Domain.Routing.NodeId(1)).Should().HaveCount(1);
    }

    [Fact]
    public void FindNearestNode_ReturnsClosest()
    {
        var trail = MakeTrail(1,
            new GeoPoint(49.0000, 19.0000),
            new GeoPoint(49.0010, 19.0010));

        var graph = TrailGraph.Build([trail]);
        var nearest = graph.FindNearestNode(new GeoPoint(49.0009, 19.0009));

        graph.GetPoint(nearest).Latitude.Should().BeApproximately(49.0010, 1e-6);
    }

    private static Trail MakeTrail(long id, params GeoPoint[] points)
    {
        return new Trail(id, $"trail-{id}", [new TrailMarking(PttkColor.Red)], points);
    }
}