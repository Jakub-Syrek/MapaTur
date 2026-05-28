namespace MapaTur.Application.Trails;

/// <summary>
/// Maps a Mapsui-style "resolution" (metres per screen pixel) to a Douglas–Peucker
/// epsilon (metres) appropriate for trail simplification at that zoom level.
/// </summary>
/// <remarks>
/// The heuristic is "epsilon ≈ ~1.5 px worth of metres", clamped between
/// <see cref="MinEpsilonMeters"/> and <see cref="MaxEpsilonMeters"/>. Below the
/// floor the gain is invisible; above the ceiling, hairpins start collapsing.
/// </remarks>
public static class ZoomEpsilonCalculator
{
    private const double TolerancePixels = 1.5;

    /// <summary>Lower bound on epsilon. Sub-metre simplification is wasted work.</summary>
    public const double MinEpsilonMeters = 1.0;

    /// <summary>Upper bound on epsilon. Above this we risk smoothing valid hairpins.</summary>
    public const double MaxEpsilonMeters = 200.0;

    /// <summary>
    /// Returns the epsilon (metres) for a Mapsui resolution (metres per pixel).
    /// Returns 0 for non-finite or non-positive resolutions — caller should then
    /// skip simplification entirely.
    /// </summary>
    public static double EpsilonMetersForResolution(double resolutionMetersPerPixel)
    {
        if (!double.IsFinite(resolutionMetersPerPixel) || resolutionMetersPerPixel <= 0.0)
        {
            return 0.0;
        }

        double raw = resolutionMetersPerPixel * TolerancePixels;
        return Math.Clamp(raw, MinEpsilonMeters, MaxEpsilonMeters);
    }
}
