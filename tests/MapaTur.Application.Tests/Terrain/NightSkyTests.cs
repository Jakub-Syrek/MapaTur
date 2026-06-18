using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour pinning for <see cref="NightSky"/>: the integration that turns a star catalog + Julian Date +
/// observer location into world-space directions for the renderer (composing sidereal time + the equatorial
/// transform). End-to-end check: Polaris (≈ the celestial pole) lands due north at altitude ≈ latitude no
/// matter the time, and the sky rotates with sidereal time.
/// </summary>
public sealed class NightSkyTests
{
    private const double Lat = 49.25;
    private const double Lon = 19.95;
    private static readonly Star Polaris = new(2.5302, 89.2641, 1.98, "Polaris", "Ursa Minor");
    private static readonly Star Equatorial = new(6.0, 0.0, 2.5, null, null);

    [Fact]
    public void StarDirections_Polaris_PointsNearDueNorthAtAltitudeLatitude()
    {
        double jd = AstronomicalTime.JulianDate(2024, 3, 1, 22.0);

        var pts = NightSky.StarDirections(new[] { Polaris }, jd, Lat, Lon);

        pts.Should().ContainSingle();
        Vector3 d = pts[0].Direction;
        d.X.Should().BeApproximately(0f, 0.03f);                                   // due north (no east/west)
        d.Y.Should().BeGreaterThan(0f);                                            // northern sky
        d.Z.Should().BeApproximately((float)Math.Sin(Lat * Math.PI / 180.0), 0.03f); // altitude ≈ latitude
    }

    [Fact]
    public void StarDirections_PreservesCountAndMagnitude()
    {
        double jd = AstronomicalTime.JulianDate(2024, 3, 1, 22.0);

        var pts = NightSky.StarDirections(new[] { Polaris, Equatorial }, jd, Lat, Lon);

        pts.Should().HaveCount(2);
        pts[0].Magnitude.Should().BeApproximately(1.98f, 1e-3f);
        pts[1].Magnitude.Should().BeApproximately(2.5f, 1e-3f);
    }

    [Fact]
    public void StarDirections_RotateWithSiderealTime()
    {
        // The same equatorial star points in a different direction six hours later (the sky has turned).
        double jd1 = AstronomicalTime.JulianDate(2024, 3, 1, 20.0);
        double jd2 = AstronomicalTime.JulianDate(2024, 3, 2, 2.0);

        Vector3 a = NightSky.StarDirections(new[] { Equatorial }, jd1, Lat, Lon)[0].Direction;
        Vector3 b = NightSky.StarDirections(new[] { Equatorial }, jd2, Lat, Lon)[0].Direction;

        Vector3.Distance(a, b).Should().BeGreaterThan(0.2f);
    }

    [Fact]
    public void StarDirectionsForLocalDate_SummerEvening_UsesUtcPlusTwoOffset()
    {
        // CEST (summer time) is UTC+2, so 23:00 local resolves to 21:00 UTC the same calendar day.
        double utcJd = AstronomicalTime.JulianDate(2026, 7, 15, 21.0);
        Vector3 expected = NightSky.StarDirections(new[] { Equatorial }, utcJd, Lat, Lon)[0].Direction;

        Vector3 actual = NightSky.StarDirectionsForLocalDate(new[] { Equatorial }, 2026, 7, 15, 23.0, Lat, Lon)[0].Direction;

        Vector3.Distance(actual, expected).Should().BeLessThan(1e-5f);
    }

    [Fact]
    public void StarDirectionsForLocalDate_WinterEvening_UsesUtcPlusOneOffset()
    {
        // CET (standard time) is UTC+1, so 23:00 local resolves to 22:00 UTC the same calendar day.
        double utcJd = AstronomicalTime.JulianDate(2026, 1, 15, 22.0);
        Vector3 expected = NightSky.StarDirections(new[] { Equatorial }, utcJd, Lat, Lon)[0].Direction;

        Vector3 actual = NightSky.StarDirectionsForLocalDate(new[] { Equatorial }, 2026, 1, 15, 23.0, Lat, Lon)[0].Direction;

        Vector3.Distance(actual, expected).Should().BeLessThan(1e-5f);
    }

    [Fact]
    public void StarDirectionsForLocalDate_NullStars_Throws()
    {
        Action act = () => NightSky.StarDirectionsForLocalDate(null!, 2026, 7, 15, 23.0, Lat, Lon);

        act.Should().Throw<ArgumentNullException>();
    }
}