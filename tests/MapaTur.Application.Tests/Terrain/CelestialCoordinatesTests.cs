using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour pinning for <see cref="CelestialCoordinates"/>: equatorial (RA/Dec) → world direction
/// (X east, Y north, Z up) given local sidereal time + observer latitude. Verified against the textbook
/// landmarks of the horizontal coordinate system rather than a sign convention: the north celestial pole
/// sits due north at altitude = latitude; a star transiting the meridian at Dec = latitude is at the zenith;
/// the celestial equator transits due south.
/// </summary>
public sealed class CelestialCoordinatesTests
{
    private const float Tol = 0.01f;
    private const double Lat = 49.25; // Tatra

    [Fact]
    public void NorthCelestialPole_IsDueNorthAtAltitudeEqualToLatitude()
    {
        // Dec = +90° → independent of hour angle: due north (y), altitude = latitude (z = sin lat).
        Vector3 v = CelestialCoordinates.EquatorialToWorld(raHours: 5.0, decDegrees: 90.0, lstHours: 10.0, latitudeDegrees: Lat);

        v.X.Should().BeApproximately(0f, Tol);
        v.Y.Should().BeApproximately((float)Math.Cos(Lat * Math.PI / 180.0), Tol);
        v.Z.Should().BeApproximately((float)Math.Sin(Lat * Math.PI / 180.0), Tol);
    }

    [Fact]
    public void StarOnMeridianAtDeclinationEqualToLatitude_IsAtZenith()
    {
        // Hour angle 0 (RA = LST) and Dec = latitude → straight overhead.
        Vector3 v = CelestialCoordinates.EquatorialToWorld(raHours: 6.0, decDegrees: Lat, lstHours: 6.0, latitudeDegrees: Lat);

        v.Z.Should().BeApproximately(1f, Tol);
        v.X.Should().BeApproximately(0f, Tol);
        v.Y.Should().BeApproximately(0f, Tol);
    }

    [Fact]
    public void CelestialEquatorOnMeridian_IsDueSouth()
    {
        // Hour angle 0, Dec 0 → due south (y negative), altitude = 90 − latitude.
        Vector3 v = CelestialCoordinates.EquatorialToWorld(raHours: 6.0, decDegrees: 0.0, lstHours: 6.0, latitudeDegrees: Lat);

        v.X.Should().BeApproximately(0f, Tol);
        v.Y.Should().BeApproximately(-(float)Math.Sin(Lat * Math.PI / 180.0), Tol);
        v.Z.Should().BeApproximately((float)Math.Cos(Lat * Math.PI / 180.0), Tol);
    }

    [Fact]
    public void Result_IsUnitLength()
    {
        Vector3 v = CelestialCoordinates.EquatorialToWorld(raHours: 3.0, decDegrees: 20.0, lstHours: 9.0, latitudeDegrees: Lat);

        v.Length().Should().BeApproximately(1f, Tol);
    }

    [Fact]
    public void FarSouthernStar_IsBelowHorizonForNorthernObserver()
    {
        // Dec −80° is never up at lat +49°: world Z (altitude sine) must be negative.
        Vector3 v = CelestialCoordinates.EquatorialToWorld(raHours: 6.0, decDegrees: -80.0, lstHours: 6.0, latitudeDegrees: Lat);

        v.Z.Should().BeLessThan(0f);
    }
}
