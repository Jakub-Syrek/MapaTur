using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Converts an equatorial sky position (right ascension / declination) to a world-space direction in the
/// renderer's frame (X east, Y north, Z up), given the local sidereal time and observer latitude. The
/// celestial sphere is built straight as a horizontal Cartesian vector (no azimuth-convention round-trip):
/// with hour angle H = (LST − RA) and latitude φ,
///   x = −cosδ·sinH,  y = cosφ·sinδ − sinφ·cosδ·cosH,  z = sinφ·sinδ + cosφ·cosδ·cosH.
/// Z is the sine of the altitude, so Z &gt; 0 means the body is above the horizon.
/// </summary>
public static class CelestialCoordinates
{
    private const double DegToRad = Math.PI / 180.0;

    /// <summary>
    /// World-space unit direction toward the sky point at (<paramref name="raHours"/>,
    /// <paramref name="decDegrees"/>) for the given local sidereal time and latitude.
    /// </summary>
    public static Vector3 EquatorialToWorld(double raHours, double decDegrees, double lstHours, double latitudeDegrees)
    {
        double hourAngle = (lstHours - raHours) * 15.0 * DegToRad; // hours → degrees → radians
        double dec = decDegrees * DegToRad;
        double lat = latitudeDegrees * DegToRad;

        double cosDec = Math.Cos(dec);
        double sinDec = Math.Sin(dec);
        double cosLat = Math.Cos(lat);
        double sinLat = Math.Sin(lat);
        double cosH = Math.Cos(hourAngle);

        double x = -cosDec * Math.Sin(hourAngle);
        double y = (cosLat * sinDec) - (sinLat * cosDec * cosH);
        double z = (sinLat * sinDec) + (cosLat * cosDec * cosH);

        return Vector3.Normalize(new Vector3((float)x, (float)y, (float)z));
    }
}