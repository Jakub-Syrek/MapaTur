using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour pinning for <see cref="AstronomicalTime"/>: Julian Date and sidereal time — the time
/// foundation the night-sky model uses to turn a real date/time + observer longitude into the rotation of
/// the celestial sphere (so RA/Dec → Alt/Az is correct). Pinned against the J2000 epoch and Meeus's worked
/// example (Astronomical Algorithms, ch. 7 &amp; 12).
/// </summary>
public sealed class AstronomicalTimeTests
{
    [Fact]
    public void JulianDate_AtJ2000Epoch_Is2451545()
    {
        // 2000-01-01 12:00 UT is the J2000.0 epoch, JD 2451545.0 by definition.
        AstronomicalTime.JulianDate(2000, 1, 1, 12.0).Should().BeApproximately(2451545.0, 1e-4);
    }

    [Fact]
    public void JulianDate_MeeusExample_1987Apr10_Is2446895point5()
    {
        // Meeus example 7.a: 1987 April 10.0 (0h UT) → JD 2446895.5.
        AstronomicalTime.JulianDate(1987, 4, 10, 0.0).Should().BeApproximately(2446895.5, 1e-4);
    }

    [Fact]
    public void GreenwichMeanSiderealTime_MeeusExample_Matches()
    {
        // Meeus example 12.a: 1987 April 10, 0h UT → GMST = 13h10m46.3668s ≈ 13.17955 h.
        double jd = AstronomicalTime.JulianDate(1987, 4, 10, 0.0);

        AstronomicalTime.GreenwichMeanSiderealTimeHours(jd).Should().BeApproximately(13.17955, 0.01);
    }

    [Fact]
    public void GreenwichMeanSiderealTime_IsWrappedIntoZeroTo24()
    {
        double g = AstronomicalTime.GreenwichMeanSiderealTimeHours(2451545.0);

        g.Should().BeInRange(0.0, 24.0);
    }

    [Fact]
    public void LocalSiderealTime_AddsOneHourPerFifteenDegreesEast()
    {
        // Longitude is added as hours (15° = 1h). At +15° E, LMST = GMST + 1h (mod 24).
        double jd = AstronomicalTime.JulianDate(1987, 4, 10, 0.0);
        double gmst = AstronomicalTime.GreenwichMeanSiderealTimeHours(jd);

        double expected = (gmst + 1.0) % 24.0;
        AstronomicalTime.LocalSiderealTimeHours(jd, 15.0).Should().BeApproximately(expected, 0.01);
    }

    [Fact]
    public void LocalSiderealTime_IsWrappedIntoZeroTo24()
    {
        // A westerly longitude that would push it negative must still wrap into [0,24).
        double jd = AstronomicalTime.JulianDate(2000, 1, 1, 0.0);

        AstronomicalTime.LocalSiderealTimeHours(jd, -179.0).Should().BeInRange(0.0, 24.0);
    }
}