using FluentAssertions;

using MapaTur.Domain.Routing;
using MapaTur.Routing.Costs;
using MapaTur.Routing.Graph;

namespace MapaTur.Routing.Tests.Costs;

public sealed class TimeCostFunctionTests
{
    private static GraphEdge Edge(double meters, double ascent, double descent, bool offTrail) =>
        new(new NodeId(1), meters, ascent, descent, offTrail);

    [Fact]
    public void Ctor_MultiplierBelowOne_Throws()
    {
        var act = () => new TimeCostFunction(0.5);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void EdgeCost_OnTrail_EqualsToblerTravelTime()
    {
        var cost = new TimeCostFunction(2.0);
        double expected = ToblerHikingFunction.TravelTimeSeconds(500.0, 80.0, 0.0);

        cost.EdgeCost(Edge(500.0, 80.0, 0.0, offTrail: false)).Should().BeApproximately(expected, 1e-9);
    }

    [Fact]
    public void EdgeCost_OffTrail_AppliesMultiplierToTravelTime()
    {
        var cost = new TimeCostFunction(3.0);
        double onTrail = ToblerHikingFunction.TravelTimeSeconds(500.0, 80.0, 0.0);

        cost.EdgeCost(Edge(500.0, 80.0, 0.0, offTrail: true)).Should().BeApproximately(onTrail * 3.0, 1e-9);
    }

    [Fact]
    public void HeuristicCost_UsesMaxToblerSpeed_AndIsAdmissibleForFlatEdge()
    {
        var cost = new TimeCostFunction();
        double maxSpeed = ToblerHikingFunction.SpeedMetersPerSecond(-0.05);

        cost.HeuristicCost(1000.0).Should().BeApproximately(1000.0 / maxSpeed, 1e-9);
        // A flat on-trail edge is slower than the optimistic max-speed heuristic — admissible.
        cost.HeuristicCost(1000.0).Should().BeLessThanOrEqualTo(cost.EdgeCost(Edge(1000.0, 0.0, 0.0, offTrail: false)));
    }
}
