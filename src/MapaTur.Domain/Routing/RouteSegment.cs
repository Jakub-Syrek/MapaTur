using MapaTur.Domain.Geography;

namespace MapaTur.Domain.Routing;

/// <summary>
/// One straight segment of a planned route, joining two consecutive graph nodes.
/// </summary>
/// <param name="From">Start point.</param>
/// <param name="To">End point.</param>
/// <param name="DistanceMeters">Great-circle distance between the two endpoints.</param>
/// <param name="AscentMeters">Positive elevation change along the segment (0 if no elevation data).</param>
/// <param name="DescentMeters">Negative elevation change along the segment as a positive value.</param>
/// <param name="DurationSeconds">Estimated travel time, as produced by the active cost function.</param>
public sealed record RouteSegment(
    GeoPoint From,
    GeoPoint To,
    double DistanceMeters,
    double AscentMeters,
    double DescentMeters,
    double DurationSeconds);
