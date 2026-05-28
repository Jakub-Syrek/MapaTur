using MapaTur.Domain.Routing;
using MapaTur.Routing.Costs;
using MapaTur.Routing.Graph;

namespace MapaTur.Routing;

/// <summary>
/// A* shortest-path search over a <see cref="TrailGraph"/>. The cost function is supplied
/// by the caller so the same router can plan for distance, time, or any future profile.
/// </summary>
public sealed class AStarRouter
{
    private readonly TrailGraph graph;

    /// <summary>
    /// Initializes a new router bound to the given graph.
    /// </summary>
    /// <param name="graph">Routing graph.</param>
    public AStarRouter(TrailGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        this.graph = graph;
    }

    /// <summary>
    /// Finds the optimal path between two graph nodes under the given cost function.
    /// Returns null when no path exists.
    /// </summary>
    /// <param name="start">Origin node.</param>
    /// <param name="goal">Destination node.</param>
    /// <param name="costFunction">Edge cost strategy.</param>
    /// <returns>A <see cref="Route"/> following the optimal path, or null if disconnected.</returns>
    public Route? FindPath(NodeId start, NodeId goal, IEdgeCostFunction costFunction)
    {
        ArgumentNullException.ThrowIfNull(costFunction);

        if (start.Value < 0 || goal.Value < 0)
        {
            return null;
        }

        if (start == goal)
        {
            return null;
        }

        var goalPoint = graph.GetPoint(goal);

        var openSet = new PriorityQueue<NodeId, double>();
        var gScore = new Dictionary<NodeId, double> { [start] = 0.0 };
        var cameFrom = new Dictionary<NodeId, (NodeId Previous, GraphEdge Edge)>();
        var closed = new HashSet<NodeId>();

        openSet.Enqueue(start, costFunction.HeuristicCost(graph.GetPoint(start).HaversineDistanceMetersTo(goalPoint)));

        while (openSet.TryDequeue(out var current, out _))
        {
            if (current == goal)
            {
                return Reconstruct(cameFrom, current, costFunction);
            }

            if (!closed.Add(current))
            {
                continue;
            }

            double currentG = gScore[current];

            foreach (var edge in graph.GetEdges(current))
            {
                double tentativeG = currentG + costFunction.EdgeCost(edge);
                if (gScore.TryGetValue(edge.To, out double existing) && tentativeG >= existing)
                {
                    continue;
                }

                gScore[edge.To] = tentativeG;
                cameFrom[edge.To] = (current, edge);

                double heuristic = costFunction.HeuristicCost(graph.GetPoint(edge.To).HaversineDistanceMetersTo(goalPoint));
                openSet.Enqueue(edge.To, tentativeG + heuristic);
            }
        }

        return null;
    }

    private Route Reconstruct(Dictionary<NodeId, (NodeId Previous, GraphEdge Edge)> cameFrom, NodeId goal, IEdgeCostFunction costFunction)
    {
        var segments = new List<RouteSegment>();
        var current = goal;

        while (cameFrom.TryGetValue(current, out var step))
        {
            var fromPoint = graph.GetPoint(step.Previous);
            var toPoint = graph.GetPoint(current);
            double durationSeconds = ToblerHikingFunction.TravelTimeSeconds(
                step.Edge.DistanceMeters,
                step.Edge.AscentMeters,
                step.Edge.DescentMeters);

            segments.Add(new RouteSegment(
                From: fromPoint,
                To: toPoint,
                DistanceMeters: step.Edge.DistanceMeters,
                AscentMeters: step.Edge.AscentMeters,
                DescentMeters: step.Edge.DescentMeters,
                DurationSeconds: durationSeconds));

            current = step.Previous;
        }

        segments.Reverse();
        return new Route(segments);
    }
}