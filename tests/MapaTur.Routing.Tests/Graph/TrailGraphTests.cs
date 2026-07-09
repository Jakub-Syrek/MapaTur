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

    [Fact]
    public void Build_SingleArgOverload_MarksEveryEdgeOnTrail()
    {
        var trail = MakeTrail(1, new GeoPoint(49.0, 19.0), new GeoPoint(49.001, 19.001));

        var graph = TrailGraph.Build([trail]);

        graph.GetEdges(new Domain.Routing.NodeId(0))[0].IsOffTrail.Should().BeFalse();
    }

    [Fact]
    public void Build_WithOffTrailTracks_FlagsThoseEdgesOffTrail()
    {
        var trail = MakeTrail(1, new GeoPoint(49.0000, 19.0000), new GeoPoint(49.0010, 19.0010));
        var offTrail = MakeTrail(2, new GeoPoint(49.0100, 19.0100), new GeoPoint(49.0110, 19.0110));

        var graph = TrailGraph.Build([trail], [offTrail]);

        // Trail nodes 0,1; off-trail nodes 2,3. The off-trail edge must carry the penalty flag.
        graph.GetEdges(new Domain.Routing.NodeId(0))[0].IsOffTrail.Should().BeFalse();
        graph.GetEdges(new Domain.Routing.NodeId(2))[0].IsOffTrail.Should().BeTrue();
    }

    [Fact]
    public void Build_OffTrailTrackSharingATrailEndpoint_ConnectsViaSnap()
    {
        // A trail and an off-trail track meeting within the snap tolerance must share the junction node,
        // so a route can leave the trail onto the imported line and back.
        var trail = MakeTrail(1, new GeoPoint(49.0000, 19.0000), new GeoPoint(49.0010, 19.0010));
        var offTrail = MakeTrail(2, new GeoPoint(49.0010, 19.00100005), new GeoPoint(49.0020, 19.0020));

        var graph = TrailGraph.Build([trail], [offTrail], snapToleranceMeters: 5.0);

        // start1, shared junction, offTrail-end = 3 unique nodes.
        graph.NodeCount.Should().Be(3);
    }

    [Fact]
    public void Build_NullOffTrailTracks_Throws()
    {
        var act = () => TrailGraph.Build([MakeTrail(1, new GeoPoint(49.0, 19.0), new GeoPoint(49.001, 19.001))], null!);

        act.Should().Throw<ArgumentNullException>();
    }

    private static Trail MakeTrail(long id, params GeoPoint[] points)
    {
        return new Trail(id, $"trail-{id}", [new TrailMarking(PttkColor.Red)], points);
    }
}