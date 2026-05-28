using MapaTur.Domain.Geography;
using MapaTur.Domain.Routing;
using MapaTur.Domain.Trails;

namespace MapaTur.Routing.Graph;

/// <summary>
/// In-memory routing graph built from a collection of trails. Points within
/// <see cref="DefaultSnapToleranceMeters"/> of each other collapse into a single node so
/// trails that share a junction are connected without requiring shared OSM node ids.
///
/// The snapping lookup uses a coarse lat/lon grid hash so building the graph is
/// O(total points) instead of O(total points²). For a typical regional download
/// (~10⁴ points) this turns minutes of work into a fraction of a second.
/// </summary>
public sealed class TrailGraph
{
    /// <summary>Default node-snapping tolerance in meters.</summary>
    public const double DefaultSnapToleranceMeters = 5.0;

    // Approximate meters per degree of latitude. Longitude varies with latitude so we
    // size the grid cell to be at least the snap tolerance in both axes near the poles.
    private const double MetersPerDegreeLatitude = 111_320.0;

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
        var index = new SpatialNodeIndex(snapToleranceMeters);

        foreach (var trail in trails)
        {
            var nodeIds = new List<NodeId>(trail.Geometry.Count);
            foreach (var point in trail.Geometry)
            {
                nodeIds.Add(index.GetOrAdd(point, nodes, adjacency));
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
    /// Finds the closest node to the given point. Uses a linear scan; for the small
    /// number of "find nearest" calls we make (start, goal) this is acceptable. If a
    /// future feature triggers many such queries, swap this for a kd-tree.
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

    /// <summary>
    /// Spatial hash for snapping near-coincident points to a single node in O(1) amortized.
    /// Each cell holds a small list of node ids that fall within it; lookups inspect at
    /// most the 9 cells around a point's cell. This is sufficient for snap tolerances
    /// well below the cell size.
    /// </summary>
    private sealed class SpatialNodeIndex
    {
        private readonly double snapToleranceMeters;
        private readonly double cellSizeDegreesLat;
        private readonly Dictionary<(long Lat, long Lon), List<int>> cells;

        public SpatialNodeIndex(double snapToleranceMeters)
        {
            this.snapToleranceMeters = snapToleranceMeters;
            // Cell side is twice the snap tolerance so a snap candidate always lies in
            // the same cell or one of the 8 neighbours.
            cellSizeDegreesLat = (snapToleranceMeters * 2.0) / MetersPerDegreeLatitude;
            cells = new Dictionary<(long, long), List<int>>(capacity: 1024);
        }

        public NodeId GetOrAdd(GeoPoint point, List<GeoPoint> nodes, List<List<GraphEdge>> adjacency)
        {
            var (latCell, lonCell) = ToCell(point);

            for (long dlat = -1; dlat <= 1; dlat++)
            {
                for (long dlon = -1; dlon <= 1; dlon++)
                {
                    if (!cells.TryGetValue((latCell + dlat, lonCell + dlon), out var bucket))
                    {
                        continue;
                    }

                    foreach (int candidateIndex in bucket)
                    {
                        if (nodes[candidateIndex].HaversineDistanceMetersTo(point) <= snapToleranceMeters)
                        {
                            return new NodeId(candidateIndex);
                        }
                    }
                }
            }

            int newIndex = nodes.Count;
            nodes.Add(point);
            adjacency.Add([]);

            if (!cells.TryGetValue((latCell, lonCell), out var ownBucket))
            {
                ownBucket = new List<int>(capacity: 2);
                cells[(latCell, lonCell)] = ownBucket;
            }
            ownBucket.Add(newIndex);
            return new NodeId(newIndex);
        }

        private (long LatCell, long LonCell) ToCell(GeoPoint point)
        {
            long latCell = (long)Math.Floor(point.Latitude / cellSizeDegreesLat);
            // Cell width in longitude shrinks with cos(latitude); for our regional use
            // the same divisor is fine and the 1-cell halo absorbs the imprecision.
            long lonCell = (long)Math.Floor(point.Longitude / cellSizeDegreesLat);
            return (latCell, lonCell);
        }
    }
}