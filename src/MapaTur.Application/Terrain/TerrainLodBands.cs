namespace MapaTur.Application.Terrain;

/// <summary>
/// Distance-based LOD selection for streaming terrain (rule #2 of the LOD architecture): maps a tile's
/// ground distance from the camera to the slippy zoom to stream it at — full 1 m detail near the eye,
/// a coarser tier mid-range, and the always-on 30 m base far away. Pure thresholds; the camera/streamer
/// supplies the distance and the persistent base covers everything beyond the detail bands. Initial values
/// are tuned on-device later — the structure (nearer ⇒ finer, monotonic) is what's pinned.
/// </summary>
public static class TerrainLodBands
{
    /// <summary>Upper edge of the full-detail band, in ground metres (inclusive).</summary>
    public const double NearMaxMeters = 500.0;

    /// <summary>Upper edge of the mid band, in ground metres (inclusive).</summary>
    public const double MidMaxMeters = 1500.0;

    /// <summary>Slippy zoom for the full-detail band (≈1.5 m/px at 49° N — near GUGiK's native 1 m).</summary>
    public const int NearZoom = 16;

    /// <summary>Slippy zoom for the mid band (≈6 m/px).</summary>
    public const int MidZoom = 14;

    /// <summary>Slippy zoom for the far/base band (≈25–30 m/px — the persistent base layer).</summary>
    public const int BaseZoom = 11;

    /// <summary>
    /// The zoom to stream a tile at, given its <paramref name="groundDistanceMeters"/> from the camera.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Distance is negative.</exception>
    public static int ZoomForDistance(double groundDistanceMeters)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(groundDistanceMeters);

        if (groundDistanceMeters <= NearMaxMeters)
        {
            return NearZoom;
        }

        return groundDistanceMeters <= MidMaxMeters ? MidZoom : BaseZoom;
    }
}