using MapaTur.Routing.Graph;

namespace MapaTur.Routing.Costs;

/// <summary>
/// Strategy that assigns a non-negative cost to traversing an edge. A* requires the
/// returned value to be admissible — never overestimate the true cost — when used with
/// the matching heuristic provided by the same strategy.
/// </summary>
public interface IEdgeCostFunction
{
    /// <summary>
    /// Computes the cost of crossing the given edge.
    /// </summary>
    /// <param name="edge">Edge to evaluate.</param>
    /// <returns>Cost, in the unit native to the strategy (e.g. seconds or meters).</returns>
    double EdgeCost(GraphEdge edge);

    /// <summary>
    /// Computes the heuristic cost from a point to the destination. Must be admissible
    /// (never exceed the true minimal cost) so A* finds the optimal path.
    /// </summary>
    /// <param name="straightLineDistanceMeters">Great-circle distance from current to goal.</param>
    /// <returns>Heuristic estimate in the same units as <see cref="EdgeCost"/>.</returns>
    double HeuristicCost(double straightLineDistanceMeters);
}