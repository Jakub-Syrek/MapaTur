using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour pinning for <see cref="SolarPosition"/>: the Sun's apparent equatorial position (Meeus ch. 25,
/// low precision). Verified against Meeus's worked example 25.b and the solstice declinations (±ε). This
/// replaces the simplified time-of-day sun arc once the night-sky upgrade wires it in.
/// </summary>
public sealed class SolarPositionTests
{
    [Fact]
    public void Equatorial_MeeusExample_1992Oct13_Matches()
    {
        // Meeus 25.b: 1992 Oct 13, 0h TD (JD 2448908.5) → RA ≈ 198.3783° (13.2252h), Dec ≈ −7.785°.
        double jd = AstronomicalTime.JulianDate(1992, 10, 13, 0.0);

        (double raHours, double decDegrees) = SolarPosition.Equatorial(jd);

        raHours.Should().BeApproximately(13.2252, 0.03);
        decDegrees.Should().BeApproximately(-7.785, 0.15);
    }

    [Fact]
    public void Equatorial_AtSummerSolstice_DeclinationIsNearPlusObliquity()
    {
        double jd = AstronomicalTime.JulianDate(2000, 6, 21, 0.0);

        SolarPosition.Equatorial(jd).DecDegrees.Should().BeApproximately(23.4, 0.4);
    }

    [Fact]
    public void Equatorial_AtWinterSolstice_DeclinationIsNearMinusObliquity()
    {
        double jd = AstronomicalTime.JulianDate(2000, 12, 21, 0.0);

        SolarPosition.Equatorial(jd).DecDegrees.Should().BeApproximately(-23.4, 0.4);
    }

    [Fact]
    public void ApparentLongitude_AtMarchEquinox_IsNearZero()
    {
        // Around the March equinox the Sun's ecliptic longitude passes 0° (start of Aries).
        double jd = AstronomicalTime.JulianDate(2000, 3, 20, 0.0);
        double lon = SolarPosition.ApparentLongitudeDegrees(jd);

        // Wrap to [-180,180] for the near-zero comparison.
        double wrapped = ((lon + 180.0) % 360.0) - 180.0;
        wrapped.Should().BeApproximately(0.0, 1.5);
    }
}
