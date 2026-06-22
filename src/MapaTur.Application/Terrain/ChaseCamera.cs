namespace MapaTur.Application.Terrain;

/// <summary>
/// Camera math for a follow ("chase") view that trails a moving subject: given the subject's travel
/// bearing, returns the orbit azimuth that seats the camera eye BEHIND it so the view looks forward along
/// the direction of motion. Pure and unit-testable; the renderer applies the result to the live camera.
/// </summary>
public static class ChaseCamera
{
    /// <summary>
    /// Orbit azimuth (radians) placing the camera eye behind a subject travelling along
    /// <paramref name="bearingDegrees"/> (compass degrees, 0 = north, clockwise). World frame is
    /// X = east, Y = north and the eye offset direction is (cos A, sin A), so the eye is set opposite the
    /// travel direction (sin β, cos β).
    /// </summary>
    public static float AzimuthRadiansForBearingDegrees(double bearingDegrees)
    {
        double b = bearingDegrees * Math.PI / 180.0;
        return MathF.Atan2((float)(-Math.Cos(b)), (float)(-Math.Sin(b)));
    }
}