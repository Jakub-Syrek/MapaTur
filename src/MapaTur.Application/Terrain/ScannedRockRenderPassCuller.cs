using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Conservative per-pass culling for already-resident scanned-rock pages. Streaming deliberately keeps
/// a prefetch ring around the main view, but those pages must not become draw calls until the current
/// main, reflection or shadow matrix can actually see them.
/// </summary>
public static class ScannedRockRenderPassCuller
{
    public static bool IsVisible(
        Matrix4x4 viewProjection,
        Vector3 cameraPosition,
        float maximumDistanceMeters,
        Vector3 worldMin,
        Vector3 worldMax)
    {
        if ((!float.IsFinite(maximumDistanceMeters) && !float.IsPositiveInfinity(maximumDistanceMeters))
            || maximumDistanceMeters <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDistanceMeters));
        }

        if (!FrustumCuller.IsAabbVisible(viewProjection, worldMin, worldMax))
        {
            return false;
        }

        if (float.IsPositiveInfinity(maximumDistanceMeters))
        {
            return true;
        }

        float dx = MathF.Max(MathF.Max(worldMin.X - cameraPosition.X, 0f), cameraPosition.X - worldMax.X);
        float dy = MathF.Max(MathF.Max(worldMin.Y - cameraPosition.Y, 0f), cameraPosition.Y - worldMax.Y);
        float dz = MathF.Max(MathF.Max(worldMin.Z - cameraPosition.Z, 0f), cameraPosition.Z - worldMax.Z);
        float maximumDistanceSquared = maximumDistanceMeters * maximumDistanceMeters;
        return (dx * dx) + (dy * dy) + (dz * dz) <= maximumDistanceSquared;
    }
}
