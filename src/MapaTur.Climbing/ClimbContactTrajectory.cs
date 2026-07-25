using System.Numerics;

namespace MapaTur.Climbing;

/// <summary>
/// Renderer-independent contact trajectories. They are expressed in world space so the same movement policy can be
/// used by the demo renderer and by terrain/surface adapters in MapaTur.
/// </summary>
public static class ClimbContactTrajectory
{
    /// <summary>
    /// Returns the temporary offset for a foot moving between two contacts. The sole first leaves the wall along the
    /// sampled surface normal, lifts against gravity, and returns exactly to the target contact at the end.
    /// </summary>
    public static Vector3 FootSwingOffset(
        float progress,
        Vector3 surfaceNormal,
        Vector3 gravity,
        float outwardClearanceMeters = 0.14f,
        float liftMeters = 0.10f)
    {
        float clampedProgress = Math.Clamp(progress, 0f, 1f);
        float arcWeight = MathF.Sin(MathF.PI * clampedProgress);
        Vector3 normal = NormalizeOrFallback(surfaceNormal, Vector3.UnitZ);
        Vector3 up = NormalizeOrFallback(-gravity, Vector3.UnitY);

        return ((normal * outwardClearanceMeters) + (up * liftMeters)) * arcWeight;
    }

    private static Vector3 NormalizeOrFallback(Vector3 value, Vector3 fallback)
    {
        float lengthSquared = value.LengthSquared();
        return lengthSquared > 1e-8f ? value / MathF.Sqrt(lengthSquared) : fallback;
    }
}