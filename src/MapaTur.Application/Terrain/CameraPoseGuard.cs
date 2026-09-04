namespace MapaTur.Application.Terrain;

/// <summary>
/// Sanity gate for a camera pose that comes from OUTSIDE the live interaction: a saved pose
/// (Preferences), a harness pose (<c>MAPATUR_START_POSE</c>) or a debug pinned camera. Lesson of
/// 2026-08-29 ("ściana przed oczyma"): a pose whose orbit TARGET sat at z = 360 m under a base whose
/// minimum is 1424 m was accepted, saved, and then restored on every launch — the camera looked down
/// through a cross-section of the terrain and the user saw a wall. The gate compares the target height
/// against the frame's elevation envelope (<c>TerrainMesh3D.MinElevationZ/MaxElevationZ</c>); an
/// implausible pose is REJECTED so the caller auto-frames instead of perpetuating garbage.
/// </summary>
public static class CameraPoseGuard
{
    /// <summary>How far BELOW the frame's minimum elevation a target may sit (valley floors in cut voids,
    /// lakes, sampling slack) before the pose is called implausible.</summary>
    public const float BelowMinMarginMeters = 150f;

    /// <summary>How far ABOVE the frame's maximum elevation a target may sit (looking at a summit from
    /// a high orbit is fine; an orbit centre kilometres in the sky is not).</summary>
    public const float AboveMaxMarginMeters = 3000f;

    /// <summary>
    /// True when <paramref name="targetZ"/> is finite and inside
    /// [<paramref name="minZ"/> − <see cref="BelowMinMarginMeters"/>, <paramref name="maxZ"/> + <see cref="AboveMaxMarginMeters"/>].
    /// A frame without elevations (min = max = 0, the <c>TerrainMesh3D</c> convention) has nothing to
    /// guard and accepts everything.
    /// </summary>
    public static bool IsTargetPlausible(float targetZ, float minZ, float maxZ)
    {
        if (!float.IsFinite(targetZ))
        {
            return false;
        }

        if (minZ == 0f && maxZ == 0f)
        {
            return true;
        }

        return targetZ >= minZ - BelowMinMarginMeters && targetZ <= maxZ + AboveMaxMarginMeters;
    }
}