using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Conservative view-frustum culling for axis-aligned bounding boxes. Used to decide which ortho
/// texture cells are potentially on screen so only those need to be uploaded to the GPU.
/// </summary>
public static class FrustumCuller
{
    /// <summary>
    /// Returns whether the world-space AABB <c>[min,max]</c> is potentially visible through the given
    /// view-projection matrix. Conservative: it never reports a visible box as culled (no false
    /// negatives), but may keep a few boxes just outside the frustum (harmless extra uploads). The box
    /// is culled only when all eight corners lie outside one and the same clip plane.
    /// </summary>
    /// <param name="viewProjection">View * projection, same convention as <see cref="Camera3D.BuildViewProjection"/>.</param>
    /// <param name="min">One AABB corner (component order is normalized, so swapped corners are tolerated).</param>
    /// <param name="max">The opposite AABB corner.</param>
    public static bool IsAabbVisible(Matrix4x4 viewProjection, Vector3 min, Vector3 max)
    {
        float minX = MathF.Min(min.X, max.X);
        float minY = MathF.Min(min.Y, max.Y);
        float minZ = MathF.Min(min.Z, max.Z);
        float maxX = MathF.Max(min.X, max.X);
        float maxY = MathF.Max(min.Y, max.Y);
        float maxZ = MathF.Max(min.Z, max.Z);

        Span<Vector4> clip = stackalloc Vector4[8];
        int i = 0;
        for (int xi = 0; xi < 2; xi++)
        {
            float x = xi == 0 ? minX : maxX;
            for (int yi = 0; yi < 2; yi++)
            {
                float y = yi == 0 ? minY : maxY;
                for (int zi = 0; zi < 2; zi++)
                {
                    float z = zi == 0 ? minZ : maxZ;
                    clip[i++] = Vector4.Transform(new Vector4(x, y, z, 1f), viewProjection);
                }
            }
        }

        // Each clip plane is a linear half-space (works regardless of the sign of w, so corners behind
        // the camera are handled correctly). The box is outside the frustum only if every corner is on
        // the wrong side of the same plane. NDC z is [0,1] (D3D convention), matching ProjectToScreen.
        return !AllOutside(clip, static c => c.X + c.W)   // left:   x >= -w
            && !AllOutside(clip, static c => c.W - c.X)   // right:  x <=  w
            && !AllOutside(clip, static c => c.Y + c.W)   // bottom: y >= -w
            && !AllOutside(clip, static c => c.W - c.Y)   // top:    y <=  w
            && !AllOutside(clip, static c => c.Z)         // near:   z >=  0
            && !AllOutside(clip, static c => c.W - c.Z);  // far:    z <=  w
    }

    private static bool AllOutside(ReadOnlySpan<Vector4> clip, Func<Vector4, float> halfSpace)
    {
        foreach (Vector4 c in clip)
        {
            if (halfSpace(c) >= 0f)
            {
                return false;
            }
        }

        return true;
    }
}