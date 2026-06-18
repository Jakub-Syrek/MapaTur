using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour pinning for <see cref="PlanetaryPosition"/>: geocentric apparent RA/Dec of the naked-eye
/// planets (Schlyter's heliocentric elements + Sun offset → geocentric). Pinned against the 2020-12-21
/// great conjunction (Jupiter ≈ Saturn ≈ 20h14m, −20.5°) and the orbital invariants that the inferior
/// planets never stray far from the Sun (Venus &lt; ~47°, Mercury &lt; ~28°).
/// </summary>
public sealed class PlanetaryPositionTests
{
    private static double SeparationDegrees(double ra1Hours, double dec1, double ra2Hours, double dec2)
    {
        double d2r = Math.PI / 180.0;
        double dAlpha = (ra1Hours - ra2Hours) * 15.0 * d2r;
        double cos = (Math.Sin(dec1 * d2r) * Math.Sin(dec2 * d2r))
            + (Math.Cos(dec1 * d2r) * Math.Cos(dec2 * d2r) * Math.Cos(dAlpha));
        return Math.Acos(Math.Clamp(cos, -1.0, 1.0)) / d2r;
    }

    [Fact]
    public void GreatConjunction_2020Dec21_JupiterAndSaturnCoincide()
    {
        double jd = AstronomicalTime.JulianDate(2020, 12, 21, 18.0);

        (double jRa, double jDec) = PlanetaryPosition.Equatorial(Planet.Jupiter, jd);
        (double sRa, double sDec) = PlanetaryPosition.Equatorial(Planet.Saturn, jd);

        // Both near 20h14m, −20.5° (Capricornus)...
        jRa.Should().BeApproximately(20.23, 0.2);
        jDec.Should().BeApproximately(-20.5, 0.7);
        // ...and within ~0.5° of each other (the conjunction was ~6 arcmin).
        SeparationDegrees(jRa, jDec, sRa, sDec).Should().BeLessThan(0.5);
    }

    [Fact]
    public void Venus_NeverStraysFarFromTheSun()
    {
        foreach (var (y, mo, day) in new[] { (2021, 3, 1), (2022, 8, 15), (2023, 6, 1) })
        {
            double jd = AstronomicalTime.JulianDate(y, mo, day, 0.0);
            (double vRa, double vDec) = PlanetaryPosition.Equatorial(Planet.Venus, jd);
            (double sRa, double sDec) = SolarPosition.Equatorial(jd);

            SeparationDegrees(vRa, vDec, sRa, sDec).Should().BeLessThan(48.0);
        }
    }

    [Fact]
    public void Mercury_NeverStraysFarFromTheSun()
    {
        foreach (var (y, mo, day) in new[] { (2021, 5, 1), (2022, 10, 1), (2023, 1, 15) })
        {
            double jd = AstronomicalTime.JulianDate(y, mo, day, 0.0);
            (double mRa, double mDec) = PlanetaryPosition.Equatorial(Planet.Mercury, jd);
            (double sRa, double sDec) = SolarPosition.Equatorial(jd);

            SeparationDegrees(mRa, mDec, sRa, sDec).Should().BeLessThan(28.0);
        }
    }
}
