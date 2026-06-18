using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Integration bridge between the astronomy core and the renderer: turns a star catalog + Julian Date +
/// observer location into world-space directions (X east, Y north, Z up) the night-sky pass can upload and
/// draw. Composes <see cref="AstronomicalTime"/> (sidereal time) with
/// <see cref="CelestialCoordinates.EquatorialToWorld"/>. The Sun/Moon/planets reuse the same two steps via
/// their own position providers.
/// </summary>
public static class NightSky
{
    /// <summary>
    /// World direction + magnitude for each catalog star at the given Julian Date and observer latitude /
    /// longitude (east positive). Direction Z &gt; 0 means the star is above the horizon.
    /// </summary>
    public static IReadOnlyList<(Vector3 Direction, float Magnitude)> StarDirections(
        IReadOnlyList<Star> stars, double julianDate, double latitudeDegrees, double longitudeDegrees)
    {
        ArgumentNullException.ThrowIfNull(stars);

        double lst = AstronomicalTime.LocalSiderealTimeHours(julianDate, longitudeDegrees);
        var result = new (Vector3, float)[stars.Count];
        for (int i = 0; i < stars.Count; i++)
        {
            Star s = stars[i];
            Vector3 dir = CelestialCoordinates.EquatorialToWorld(s.RaHours, s.DecDegrees, lst, latitudeDegrees);
            result[i] = (dir, (float)s.Magnitude);
        }

        return result;
    }

    /// <summary>
    /// Convenience overload for the renderer: world direction + magnitude for each star at a <em>local</em>
    /// wall-clock date and hour in the app's region (Central European Time, DST-aware). The renderer supplies
    /// today's calendar date and the time-slider hour as local time, plus the scene-centre latitude /
    /// longitude (e.g. the mesh's <c>ProjectionAnchor</c>). Converts the local hour to UTC via
    /// <see cref="CentralEuropeanTime"/>, builds the Julian Date, then defers to <see cref="StarDirections"/>.
    /// </summary>
    /// <remarks>
    /// The Julian Date formula is continuous in the UT hour, so a local hour near midnight that subtracts the
    /// offset below zero shifts smoothly into the previous calendar day — no manual date rollover needed.
    /// </remarks>
    public static IReadOnlyList<(Vector3 Direction, float Magnitude)> StarDirectionsForLocalDate(
        IReadOnlyList<Star> stars, int year, int month, int day, double localHour,
        double latitudeDegrees, double longitudeDegrees)
    {
        ArgumentNullException.ThrowIfNull(stars);

        double hourUtc = localHour - CentralEuropeanTime.UtcOffsetHours(year, month, day);
        double julianDate = AstronomicalTime.JulianDate(year, month, day, hourUtc);
        return StarDirections(stars, julianDate, latitudeDegrees, longitudeDegrees);
    }
}