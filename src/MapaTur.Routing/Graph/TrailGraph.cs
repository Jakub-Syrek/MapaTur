using MapaTur.Domain.Geography;
using MapaTur.Domain.Routing;
using MapaTur.Domain.Trails;

namespace MapaTur.Routing.Graph;

/// <summary>
/// In-memory routing graph built from a collection of trails. Points within
/// <see cref="DefaultSnapToleranceMeters"/> of each other collapse into a single node so
/// trails that share a junction are connected without requiring shared OSM node ids.
/// </summary>
public sealed class TrailGraph
{
    /// <summary>Default node-snapping tolerance in meters.</summary>
    public const double DefaultSnapToleranceMeters = 5.0;

    private readonly List<GeoPoint> nodes;
    private readonly List<List<GraphEdge>> adjacency;

    private TrailGraph(List<GeoPoint> nodes, List<List<GraphEdge>> adjacency)
    {
        this.nodes = nodes;
        this.adjacency = adjacency;
    }

    /// <summary>Total number of nodes in the graph.</summary>
    public int NodeCount => nodes.Count;

    /// <summary>
    /// Builds a routing graph from the given trails. Each consecutive pair of geometry
    /// points becomes a pair of directed edges (forward and reverse).
    /// </summary>
    /// <param name="trails">Trails to ingest.</param>
    /// <param name="snapToleranceMeters">Snapping tolerance in meters.</param>
    /// <returns>A new graph instance.</returns>
    public static TrailGraph Build(IEnumerable<Trail> trails, double snapToleranceMeters = DefaultSnapToleranceMeters)
    {
        ArgumentNullException.ThrowIfNull(trails);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(snapToleranceMeters);

        var nodes = new List<GeoPoint>();
        var adjacency = new List<List<GraphEdge>>();

        foreach (var trail in trails)
        {
            var nodeIds = new List<NodeId>(trail.Geometry.Count);
            foreach (var point in trail.Geometry)
            {
                nodeIds.Add(GetOrAddNode(point, nodes, adjacency, snapToleranceMeters));
            }

            for (int i = 0; i < nodeIds.Count - 1; i++)
            {
                AddBidirectionalEdge(adjacency, nodes, nodeIds[i], nodeIds[i + 1], isOffTrail: false);
            }
        }

        return new TrailGraph(nodes, adjacency);
    }

    /// <summary>Returns the geographic position of a node.</summary>
    /// <param name="nodeId">Node identifier.</param>
    /// <returns>Position.</returns>
    public GeoPoint GetPoint(NodeId nodeId) => nodes[nodeId.Value];

    /// <summary>Returns the outgoing edges of a node.</summary>
    /// <param name="nodeId">Source node.</param>
    /// <returns>Edges leaving the node.</returns>
    public IReadOnlyList<GraphEdge> GetEdges(NodeId nodeId) => adjacency[nodeId.Value];

    /// <summary>
    /// Finds the closest node to the given point. Returns <see cref="NodeId.None"/> when
    /// the graph is empty.
    /// </summary>
    /// <param name="point">Reference point.</param>
    /// <returns>Closest node identifier.</returns>
    public NodeId FindNearestNode(GeoPoint point)
    {
        if (nodes.Count == 0)
        {
            return NodeId.None;
        }

        int bestIndex = 0;
        double bestDistance = nodes[0].HaversineDistanceMetersTo(point);
        for (int i = 1; i < nodes.Count; i++)
        {
            double distance = nodes[i].HaversineDistanceMetersTo(point);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = i;
            }
        }

        return new NodeId(bestIndex);
    }

    private static NodeId GetOrAddNode(GeoPoint point, List<GeoPoint> nodes, List<List<GraphEdge>> adjacency, double snapToleranceMeters)
    {
        for (int i = 0; i < nodes.Count; i++)
        {
            if (nodes[i].HaversineDistanceMetersTo(point) <= snapToleranceMeters)
            {
                return new NodeId(i);
            }
        }

        nodes.Add(point);
        adjacency.Add([]);
        return new NodeId(nodes.Count - 1);
    }

    private static void AddBidirectionalEdge(List<List<GraphEdge>> adjacency, List<GeoPoint> nodes, NodeId from, NodeId to, bool isOffTrail)
    {
        if (from.Value == to.Value)
        {
            return;
        }

        var fromPoint = nodes[from.Value];
        var toPoint = nodes[to.Value];
        double distance = fromPoint.HaversineDistanceMetersTo(toPoint);
        double elevationDelta = (toPoint.ElevationMeters ?? 0.0) - (fromPoint.ElevationMeters ?? 0.0);

        adjacency[from.Value].Add(new GraphEdge(
            To: to,
            DistanceMeters: distance,
            AscentMeters: Math.Max(0.0, elevationDelta),
            DescentMeters: Math.Max(0.0, -elevationDelta),
            IsOffTrail: isOffTrail));

        adjacency[to.Value].Add(new GraphEdge(
            To: from,
            DistanceMeters: distance,
            AscentMeters: Math.Max(0.0, -elevationDelta),
            DescentMeters: Math.Max(0.0, elevationDelta),
            IsOffTrail: isOffTrail));
    }
}
