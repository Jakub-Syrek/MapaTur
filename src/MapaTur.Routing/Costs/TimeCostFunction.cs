using MapaTur.Routing.Graph;

namespace MapaTur.Routing.Costs;

/// <summary>
/// Cost equals estimated travel time in seconds, using <see cref="ToblerHikingFunction"/>.
/// The heuristic converts straight-line distance into a lower-bound time by assuming the
/// fastest possible speed (the maximum of Tobler at slope ~= -0.05).
/// </summary>
public sealed class TimeCostFunction : IEdgeCostFunction
{
    private static readonly double MaxSpeedMetersPerSecond = ToblerHikingFunction.SpeedMetersPerSecond(slope: -0.05);

    private readonly double offTrailPenaltyMultiplier;

    /// <summary>
    /// Initializes a new instance of the cost function.
    /// </summary>
    /// <param name="offTrailPenaltyMultiplier">Multiplier applied to off-trail edge times. Defaults to 2.0.</param>
    public TimeCostFunction(double offTrailPenaltyMultiplier = 2.0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(offTrailPenaltyMultiplier, 1.0);
        this.offTrailPenaltyMultiplier = offTrailPenaltyMultiplier;
    }

    /// <inheritdoc />
    public double EdgeCost(GraphEdge edge)
    {
        double seconds = ToblerHikingFunction.TravelTimeSeconds(edge.DistanceMeters, edge.AscentMeters, edge.DescentMeters);
        return edge.IsOffTrail ? seconds * offTrailPenaltyMultiplier : seconds;
    }

    /// <inheritdoc />
    public double HeuristicCost(double straightLineDistanceMeters)
    {
        return straightLineDistanceMeters / MaxSpeedMetersPerSecond;
    }
}