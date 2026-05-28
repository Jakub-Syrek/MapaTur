using MapaTur.Domain.Routing;

namespace MapaTur.Routing.Graph;

/// <summary>
/// Directed edge in the routing graph. The reverse edge is added separately by the builder
/// so cost functions can model asymmetric profiles (e.g. faster going downhill).
/// </summary>
/// <param name="To">Destination node.</param>
/// <param name="DistanceMeters">Horizontal distance covered by the edge.</param>
/// <param name="AscentMeters">Positive elevation change going from source to destination.</param>
/// <param name="DescentMeters">Negative elevation change as a positive value.</param>
/// <param name="IsOffTrail">True when this edge does not follow a marked trail.</param>
public readonly record struct GraphEdge(
    NodeId To,
    double DistanceMeters,
    double AscentMeters,
    double DescentMeters,
    bool IsOffTrail);