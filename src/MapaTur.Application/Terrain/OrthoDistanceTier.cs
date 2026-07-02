namespace MapaTur.Application.Terrain;

/// <summary>
/// Ortho cells are geographically huge (~16 km) but were all downsampled to one FLAT resolution cap regardless
/// of camera distance — a cell right under the camera got the same coarse texture as one 50 km away, which
/// reads as blocky pixelation up close. This picks a per-cell resolution cap from camera distance instead: a
/// cell the camera is near keeps the source's full ("near") resolution, a distant cell is downsampled harder
/// ("far") — the same distance-graduated idea as the terrain detail layer's <c>DistanceFade</c>, applied to
/// ortho texture resolution instead of shading amplitude.
/// </summary>
public static class OrthoDistanceTier
{
    /// <summary>Resolution cap (longest side, px) for a cell the camera is near — the source's retained "master" resolution, no further reduction.</summary>
    public const int NearCapPx = 8192;

    /// <summary>Resolution cap (longest side, px) for a cell far from the camera.</summary>
    public const int FarCapPx = 2048;

    // A cell PROMOTES to the near tier once the camera is within this distance of its bounds...
    private const float EnterNearDistanceMeters = 10_000f;

    // ...but only DEMOTES back to far once past this larger distance. The gap between the two is a dead-band:
    // without it, hovering exactly at one threshold would flip the tier (and trigger a re-downsample + GPU
    // re-upload) every frame the distance jitters across it.
    private const float ExitNearDistanceMeters = 14_000f;

    /// <summary>
    /// The resolution cap a cell should use, given the cap it is CURRENTLY at (0 = never uploaded — treated
    /// like the far tier so a cell's very first upload never wastes a near-tier download on ground that turns
    /// out to be far away) and its current distance from the camera, in metres.
    /// </summary>
    public static int DesiredCapPx(int currentCapPx, float distanceMeters)
    {
        if (currentCapPx == NearCapPx)
        {
            return distanceMeters > ExitNearDistanceMeters ? FarCapPx : NearCapPx;
        }

        return distanceMeters <= EnterNearDistanceMeters ? NearCapPx : FarCapPx;
    }
}