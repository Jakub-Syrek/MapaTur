using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour pinning for <see cref="LunarPosition"/>: the Moon's apparent equatorial position (Schlyter's
/// compact lunar theory with the main periodic perturbations, ~arcmin) and its illuminated fraction (phase).
/// Verified against Meeus's worked example 47.a and the known new/full Moons of January 2000.
/// </summary>
public sealed class LunarPositionTests
{
    [Fact]
    public void Equatorial_MeeusExample_1992Apr12_Matches()
    {
        // Meeus 47.a: 1992 Apr 12, 0h TD → apparent RA ≈ 134.6885° (8.9792h), Dec ≈ +13.768°.
        double jd = AstronomicalTime.JulianDate(1992, 4, 12, 0.0);

        (double raHours, double decDegrees) = LunarPosition.Equatorial(jd);

        raHours.Should().BeApproximately(8.9792, 0.06);
        decDegrees.Should().BeApproximately(13.768, 0.35);
    }

    [Fact]
    public void IlluminatedFraction_AtFullMoon_IsNearOne()
    {
        // 2000 Jan 21 ~04:40 UT — full Moon (total lunar eclipse).
        double jd = AstronomicalTime.JulianDate(2000, 1, 21, 4.7);

        LunarPosition.IlluminatedFraction(jd).Should().BeGreaterThan(0.97);
    }

    [Fact]
    public void IlluminatedFraction_AtNewMoon_IsNearZero()
    {
        // 2000 Jan 6 ~18:14 UT — new Moon.
        double jd = AstronomicalTime.JulianDate(2000, 1, 6, 18.2);

        LunarPosition.IlluminatedFraction(jd).Should().BeLessThan(0.03);
    }

    [Fact]
    public void IlluminatedFraction_IsInUnitRange()
    {
        double jd = AstronomicalTime.JulianDate(2000, 1, 13, 0.0);

        LunarPosition.IlluminatedFraction(jd).Should().BeInRange(0.0, 1.0);
    }
}