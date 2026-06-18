namespace MapaTur.Application.Terrain;

/// <summary>
/// Time foundation for the real night-sky model: Julian Date and (Greenwich / local) mean sidereal time.
/// Sidereal time is the rotation angle of the celestial sphere for an observer, so it converts a star's
/// fixed right ascension into the local hour angle that drives the Alt/Az transform. Formulas from Meeus,
/// <i>Astronomical Algorithms</i> (ch. 7 Julian Day, ch. 12 sidereal time). Pure/deterministic — the caller
/// passes the date/time (the app supplies today's date) so the model stays unit-testable.
/// </summary>
public static class AstronomicalTime
{
    /// <summary>
    /// Julian Date for a Gregorian calendar date and fractional UT hour (Meeus 7.1). Valid for the modern
    /// Gregorian range used by the app.
    /// </summary>
    public static double JulianDate(int year, int month, int day, double hourUtc)
    {
        if (month <= 2)
        {
            year -= 1;
            month += 12;
        }
        int a = year / 100;
        int b = 2 - a + (a / 4);
        return Math.Floor(365.25 * (year + 4716))
            + Math.Floor(30.6001 * (month + 1))
            + day + (hourUtc / 24.0) + b - 1524.5;
    }

    /// <summary>
    /// Greenwich Mean Sidereal Time in hours [0,24) for a Julian Date (Meeus 12.4).
    /// </summary>
    public static double GreenwichMeanSiderealTimeHours(double julianDate)
    {
        double t = (julianDate - 2451545.0) / 36525.0;
        double gmstDeg = 280.46061837
            + (360.98564736629 * (julianDate - 2451545.0))
            + (0.000387933 * t * t)
            - (t * t * t / 38710000.0);
        gmstDeg = ((gmstDeg % 360.0) + 360.0) % 360.0;
        return gmstDeg / 15.0;
    }

    /// <summary>
    /// Local Mean Sidereal Time in hours [0,24): GMST plus the observer's longitude (east positive),
    /// converted to hours (15° = 1h).
    /// </summary>
    public static double LocalSiderealTimeHours(double julianDate, double longitudeDegreesEast)
    {
        double lmst = GreenwichMeanSiderealTimeHours(julianDate) + (longitudeDegreesEast / 15.0);
        lmst %= 24.0;
        return lmst < 0.0 ? lmst + 24.0 : lmst;
    }
}