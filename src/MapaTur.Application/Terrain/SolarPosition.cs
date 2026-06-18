namespace MapaTur.Application.Terrain;

/// <summary>
/// Apparent equatorial position of the Sun for a Julian Date — Meeus, <i>Astronomical Algorithms</i> ch. 25
/// (low-precision method, good to ~0.01°). Drives the real day/night cycle: combine with
/// <see cref="CelestialCoordinates.EquatorialToWorld"/> for the world-space sun direction, and the apparent
/// ecliptic longitude feeds the Moon's phase.
/// </summary>
public static class SolarPosition
{
    private const double DegToRad = Math.PI / 180.0;
    private const double RadToDeg = 180.0 / Math.PI;

    /// <summary>Apparent right ascension (hours, [0,24)) and declination (degrees) of the Sun.</summary>
    public static (double RaHours, double DecDegrees) Equatorial(double julianDate)
    {
        double t = (julianDate - 2451545.0) / 36525.0;
        double lambda = ApparentLongitudeDegrees(julianDate) * DegToRad;
        double omega = 125.04 - (1934.136 * t);
        double epsilon = (MeanObliquityDegrees(t) + (0.00256 * Math.Cos(omega * DegToRad))) * DegToRad;

        double ra = Math.Atan2(Math.Cos(epsilon) * Math.Sin(lambda), Math.Cos(lambda));
        double dec = Math.Asin(Math.Sin(epsilon) * Math.Sin(lambda));

        double raDeg = (((ra * RadToDeg) % 360.0) + 360.0) % 360.0;
        return (raDeg / 15.0, dec * RadToDeg);
    }

    /// <summary>Apparent ecliptic longitude of the Sun in degrees [0,360).</summary>
    public static double ApparentLongitudeDegrees(double julianDate)
    {
        double t = (julianDate - 2451545.0) / 36525.0;
        double l0 = 280.46646 + (36000.76983 * t) + (0.0003032 * t * t);
        double m = (357.52911 + (35999.05029 * t) - (0.0001537 * t * t)) * DegToRad;

        double c = ((1.914602 - (0.004817 * t) - (0.000014 * t * t)) * Math.Sin(m))
            + ((0.019993 - (0.000101 * t)) * Math.Sin(2 * m))
            + (0.000289 * Math.Sin(3 * m));

        double trueLon = l0 + c;
        double omega = 125.04 - (1934.136 * t);
        double lambda = trueLon - 0.00569 - (0.00478 * Math.Sin(omega * DegToRad));
        return ((lambda % 360.0) + 360.0) % 360.0;
    }

    private static double MeanObliquityDegrees(double t)
        => 23.439291 - (0.0130042 * t) - (1.64e-7 * t * t) + (5.04e-7 * t * t * t);
}