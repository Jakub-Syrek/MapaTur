namespace MapaTur.Domain.Tracks;

/// <summary>
/// Aggregated elevation statistics for a track or planned route.
/// All values are in meters.
/// </summary>
/// <param name="MinElevationMeters">Minimum elevation observed.</param>
/// <param name="MaxElevationMeters">Maximum elevation observed.</param>
/// <param name="TotalAscentMeters">Total positive elevation change.</param>
/// <param name="TotalDescentMeters">Total negative elevation change (positive value).</param>
public readonly record struct ElevationProfile(
    double MinElevationMeters,
    double MaxElevationMeters,
    double TotalAscentMeters,
    double TotalDescentMeters)
{
    /// <summary>
    /// Empty profile used as a neutral starting value.
    /// </summary>
    public static readonly ElevationProfile Empty = new(double.NaN, double.NaN, 0.0, 0.0);

    /// <summary>
    /// Computes an elevation profile from an ordered sequence of points.
    /// Points without elevation are skipped. Returns <see cref="Empty"/> when no points carry elevation.
    /// </summary>
    /// <param name="points">Track points in chronological order.</param>
    /// <returns>Elevation profile aggregated from the points.</returns>
    public static ElevationProfile FromPoints(IEnumerable<TrackPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);

        double? min = null;
        double? max = null;
        double ascent = 0.0;
        double descent = 0.0;
        double? previous = null;

        foreach (var point in points)
        {
            if (point.Position.ElevationMeters is not { } elevation)
            {
                continue;
            }

            if (min is null || elevation < min)
            {
                min = elevation;
            }

            if (max is null || elevation > max)
            {
                max = elevation;
            }

            if (previous is { } prev)
            {
                double delta = elevation - prev;
                if (delta > 0.0)
                {
                    ascent += delta;
                }
                else
                {
                    descent += -delta;
                }
            }

            previous = elevation;
        }

        if (min is null)
        {
            return Empty;
        }

        return new ElevationProfile(min.Value, max!.Value, ascent, descent);
    }
}