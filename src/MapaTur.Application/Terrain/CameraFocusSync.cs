namespace MapaTur.Application.Terrain;

/// <summary>
/// Converts between a 3D orbit camera's <see cref="Camera3D.Distance"/> and a 2D Mapsui
/// resolution (mercator metres per pixel) so that toggling between the 3D terrain view and
/// the flat 2D map preserves the user's zoom level — "po wybraniu kąta i powiększenia".
/// </summary>
/// <remarks>
/// Geometry: looking at the camera <see cref="Camera3D.Target"/> from <c>distance</c>
/// metres with vertical field of view <c>fovY</c>, the frustum's vertical ground span at the
/// target plane is <c>2·distance·tan(fovY/2)</c>. Spread across the viewport height in pixels
/// that yields ground metres per pixel. Mapsui resolution is expressed in *mercator* metres per
/// pixel, which relates to true ground metres per pixel by a factor of <c>1/cos(latitude)</c>
/// (mercator stretches toward the poles). Pitch is intentionally ignored: the mapping only needs
/// to be a smooth, monotonic, invertible "zoom feel", not a pixel-exact match — the dominant
/// user complaint is the *focal point*, with zoom a secondary nicety.
/// </remarks>
public static class CameraFocusSync
{
    /// <summary>
    /// Maps a 3D camera distance to the equivalent 2D map resolution (mercator metres per pixel).
    /// </summary>
    /// <param name="distance">Camera distance from its target, in metres.</param>
    /// <param name="fovYRadians">Camera vertical field of view, in radians.</param>
    /// <param name="viewportHeightPixels">Rendered viewport height in pixels.</param>
    /// <param name="latitudeDegrees">Latitude of the focus point (for the mercator scale factor).</param>
    public static double DistanceToResolution(
        double distance, double fovYRadians, double viewportHeightPixels, double latitudeDegrees)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(viewportHeightPixels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fovYRadians);

        double groundSpanMeters = 2.0 * distance * Math.Tan(fovYRadians / 2.0);
        double groundMetersPerPixel = groundSpanMeters / viewportHeightPixels;
        return groundMetersPerPixel / Math.Cos(latitudeDegrees * Math.PI / 180.0);
    }

    /// <summary>
    /// Inverse of <see cref="DistanceToResolution"/>: maps a 2D map resolution back to a camera distance.
    /// </summary>
    /// <param name="resolution">Mapsui resolution in mercator metres per pixel.</param>
    /// <param name="fovYRadians">Camera vertical field of view, in radians.</param>
    /// <param name="viewportHeightPixels">Rendered viewport height in pixels.</param>
    /// <param name="latitudeDegrees">Latitude of the focus point (for the mercator scale factor).</param>
    public static double ResolutionToDistance(
        double resolution, double fovYRadians, double viewportHeightPixels, double latitudeDegrees)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(viewportHeightPixels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fovYRadians);

        double groundMetersPerPixel = resolution * Math.Cos(latitudeDegrees * Math.PI / 180.0);
        double groundSpanMeters = groundMetersPerPixel * viewportHeightPixels;
        return groundSpanMeters / (2.0 * Math.Tan(fovYRadians / 2.0));
    }
}