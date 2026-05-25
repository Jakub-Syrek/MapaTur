using MapaTur.Domain.Geography;

namespace MapaTur.Domain.Routing;

/// <summary>
/// A planned route between two points, expressed as an ordered sequence of segments.
/// Distance, ascent, descent and total duration are aggregated for quick display.
/// </summary>
public sealed class Route
{
    /// <summary>
    /// Initializes a new route.
    /// </summary>
    /// <param name="segments">Ordered route segments. Must contain at least one segment.</param>
    /// <exception cref="ArgumentException">Thrown when the segment list is empty.</exception>
    public Route(IReadOnlyList<RouteSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Count == 0)
        {
            throw new ArgumentException("Route must contain at least one segment.", nameof(segments));
        }

        Segments = segments;
        TotalDistanceMeters = segments.Sum(segment => segment.DistanceMeters);
        TotalAscentMeters = segments.Sum(segment => segment.AscentMeters);
        TotalDescentMeters = segments.Sum(segment => segment.DescentMeters);
        TotalDurationSeconds = segments.Sum(segment => segment.DurationSeconds);
    }

    /// <summary>Ordered list of segments forming the route.</summary>
    public IReadOnlyList<RouteSegment> Segments { get; }

    /// <summary>Origin point of the route.</summary>
    public GeoPoint Start => Segments[0].From;

    /// <summary>Destination point of the route.</summary>
    public GeoPoint End => Segments[^1].To;

    /// <summary>Sum of horizontal distances of all segments, in meters.</summary>
    public double TotalDistanceMeters { get; }

    /// <summary>Sum of positive elevation changes along the route, in meters.</summary>
    public double TotalAscentMeters { get; }

    /// <summary>Sum of negative elevation changes along the route, in meters.</summary>
    public double TotalDescentMeters { get; }

    /// <summary>Estimated total travel time in seconds.</summary>
    public double TotalDurationSeconds { get; }

    /// <summary>
    /// Returns the polyline of geographic points that the route visits, in order.
    /// </summary>
    /// <returns>Polyline points.</returns>
    public IReadOnlyList<GeoPoint> ToPolyline()
    {
        var polyline = new List<GeoPoint>(Segments.Count + 1) { Segments[0].From };
        foreach (var segment in Segments)
        {
            polyline.Add(segment.To);
        }
        return polyline;
    }
}
