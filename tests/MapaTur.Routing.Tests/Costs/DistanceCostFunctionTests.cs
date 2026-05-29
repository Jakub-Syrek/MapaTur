using FluentAssertions;

using MapaTur.Domain.Routing;
using MapaTur.Routing.Costs;
using MapaTur.Routing.Graph;

namespace MapaTur.Routing.Tests.Costs;

public sealed class DistanceCostFunctionTests
{
    private static GraphEdge Edge(double meters, bool offTrail) =>
        new(new NodeId(1), meters, 0.0, 0.0, offTrail);

    [Fact]
    public void Ctor_MultiplierBelowOne_Throws()
    {
        var act = () => new DistanceCostFunction(0.9);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void EdgeCost_OnTrail_EqualsDistance()
    {
        var cost = new DistanceCostFunction(2.0);

        cost.EdgeCost(Edge(150.0, offTrail: false)).Should().Be(150.0);
    }

    [Fact]
    public void EdgeCost_OffTrail_AppliesMultiplier()
    {
        var cost = new DistanceCostFunction(2.5);

        cost.EdgeCost(Edge(150.0, offTrail: true)).Should().Be(150.0 * 2.5);
    }

    [Fact]
    public void HeuristicCost_ReturnsStraightLineDistanceUnchanged()
    {
        var cost = new DistanceCostFunction();

        cost.HeuristicCost(1234.5).Should().Be(1234.5);
    }

    [Fact]
    public void HeuristicCost_NeverExceedsOnTrailEdgeCost_SoItIsAdmissible()
    {
        var cost = new DistanceCostFunction();

        cost.HeuristicCost(150.0).Should().BeLessThanOrEqualTo(cost.EdgeCost(Edge(150.0, offTrail: false)));
    }
}
