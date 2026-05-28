namespace MapaTur.Routing.Costs;

/// <summary>
/// Tobler's hiking function: estimates walking speed on a slope. Returns m/s.
/// Formula: v = 6 * exp(-3.5 * |slope + 0.05|) km/h, then converted to m/s.
/// Slope is the tangent (rise over run) of the terrain.
/// Reference: Tobler, W. (1993). Three presentations on geographical analysis and modeling.
/// </summary>
public static class ToblerHikingFunction
{
    private const double MaxKmPerHour = 6.0;
    private const double SlopeOffset = 0.05;
    private const double SlopeDecay = 3.5;
    private const double KmPerHourToMetersPerSecond = 1000.0 / 3600.0;

    /// <summary>
    /// Returns walking speed in meters per second for the given slope.
    /// </summary>
    /// <param name="slope">Rise over run; positive for uphill, negative for downhill.</param>
    /// <returns>Walking speed in m/s. Always positive.</returns>
    public static double SpeedMetersPerSecond(double slope)
    {
        double kmh = MaxKmPerHour * Math.Exp(-SlopeDecay * Math.Abs(slope + SlopeOffset));
        return kmh * KmPerHourToMetersPerSecond;
    }

    /// <summary>
    /// Estimates time to traverse a segment of the given distance with the given net elevation
    /// change. Returns seconds. A zero-distance segment returns zero.
    /// </summary>
    /// <param name="distanceMeters">Horizontal distance of the segment.</param>
    /// <param name="ascentMeters">Positive elevation change.</param>
    /// <param name="descentMeters">Negative elevation change (positive value).</param>
    /// <returns>Estimated time in seconds.</returns>
    public static double TravelTimeSeconds(double distanceMeters, double ascentMeters, double descentMeters)
    {
        if (distanceMeters <= 0.0)
        {
            return 0.0;
        }

        double netElevation = ascentMeters - descentMeters;
        double slope = netElevation / distanceMeters;
        return distanceMeters / SpeedMetersPerSecond(slope);
    }
}