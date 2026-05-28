using MapaTur.Routing.Graph;

namespace MapaTur.Routing.Costs;

/// <summary>
/// Cost equals horizontal distance. Off-trail edges, when present, are penalised by
/// a configurable multiplier. The heuristic uses the haversine distance directly,
/// which is always admissible because shortest path cannot beat the great circle.
/// </summary>
public sealed class DistanceCostFunction : IEdgeCostFunction
{
    private readonly double offTrailPenaltyMultiplier;

    /// <summary>
    /// Initializes a new instance of the cost function.
    /// </summary>
    /// <param name="offTrailPenaltyMultiplier">Multiplier applied to off-trail edges. Defaults to 2.0.</param>
    public DistanceCostFunction(double offTrailPenaltyMultiplier = 2.0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(offTrailPenaltyMultiplier, 1.0);
        this.offTrailPenaltyMultiplier = offTrailPenaltyMultiplier;
    }

    /// <inheritdoc />
    public double EdgeCost(GraphEdge edge)
    {
        return edge.IsOffTrail ? edge.DistanceMeters * offTrailPenaltyMultiplier : edge.DistanceMeters;
    }

    /// <inheritdoc />
    public double HeuristicCost(double straightLineDistanceMeters) => straightLineDistanceMeters;
}