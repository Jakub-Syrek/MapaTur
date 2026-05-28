using MapaTur.Domain.Geography;

namespace MapaTur.Domain.Tracks;

/// <summary>
/// A recorded GPS track imported from an external device (e.g. Garmin TCX file).
/// Immutable aggregate; create a new instance to represent changes.
/// </summary>
public sealed class Track
{
    /// <summary>
    /// Initializes a new track.
    /// </summary>
    /// <param name="id">Stable identifier for the track.</param>
    /// <param name="name">Human-readable name (e.g. derived from filename or TCX activity id).</param>
    /// <param name="points">Track points in chronological order. Must contain at least one point.</param>
    /// <exception cref="ArgumentException">Thrown when the point list is empty.</exception>
    public Track(Guid id, string name, IReadOnlyList<TrackPoint> points)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(points);

        if (points.Count == 0)
        {
            throw new ArgumentException("Track must contain at least one point.", nameof(points));
        }

        Id = id;
        Name = name;
        Points = points;
    }

    /// <summary>Stable identifier.</summary>
    public Guid Id { get; }

    /// <summary>Human-readable name.</summary>
    public string Name { get; }

    /// <summary>Track points in chronological order.</summary>
    public IReadOnlyList<TrackPoint> Points { get; }

    /// <summary>UTC instant of the first recorded point.</summary>
    public DateTimeOffset StartedAt => Points[0].Timestamp;

    /// <summary>UTC instant of the last recorded point.</summary>
    public DateTimeOffset EndedAt => Points[^1].Timestamp;

    /// <summary>
    /// Total horizontal distance along the track, computed by summing haversine distances
    /// between consecutive points. Elevation gain is excluded.
    /// </summary>
    /// <returns>Distance in meters.</returns>
    public double ComputeDistanceMeters()
    {
        double total = 0.0;
        for (int i = 1; i < Points.Count; i++)
        {
            total += Points[i - 1].Position.HaversineDistanceMetersTo(Points[i].Position);
        }
        return total;
    }

    /// <summary>
    /// Computes the elevation profile (min/max/ascent/descent) for this track.
    /// </summary>
    /// <returns>Aggregated profile.</returns>
    public ElevationProfile ComputeElevationProfile()
    {
        return ElevationProfile.FromPoints(Points);
    }
}