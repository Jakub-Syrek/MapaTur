using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Low-resolution screen-space depth grid used to test trail / route / climbing
/// vertices against the projected mesh for back-of-mountain occlusion. Each bin
/// stores the minimum NDC depth written to it (closest visible mesh surface);
/// a vertex whose depth exceeds <c>bin + epsilon</c> is "behind" the mesh.
/// </summary>
/// <remarks>
/// The grid is binned coarsely (typically 64×64 over a 1280×720 viewport, so
/// each bin covers ~20×11 px). Far cheaper than rasterising every projected
/// triangle pixel-by-pixel, accurate enough for "is this trail behind a
/// mountain" — the depth gradient at mountain silhouettes is large compared
/// to any sub-bin precision loss.
/// </remarks>
public sealed class ScreenDepthMap
{
    private const float NoDepth = float.PositiveInfinity;

    private readonly float[] depths;
    private readonly int binsX;
    private readonly int binsY;
    private float screenWidth = 1f;
    private float screenHeight = 1f;

    /// <summary>Number of horizontal bins.</summary>
    public int BinsX => binsX;

    /// <summary>Number of vertical bins.</summary>
    public int BinsY => binsY;

    public ScreenDepthMap(int binsX, int binsY)
    {
        if (binsX <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(binsX), binsX, "Bin counts must be positive.");
        }
        if (binsY <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(binsY), binsY, "Bin counts must be positive.");
        }

        this.binsX = binsX;
        this.binsY = binsY;
        depths = new float[binsX * binsY];
    }

    /// <summary>
    /// Tells the grid how big the screen is so coordinate→bin mapping is correct.
    /// Call this once per frame before <see cref="Reset"/> + writes.
    /// </summary>
    public void Configure(float screenWidth, float screenHeight)
    {
        this.screenWidth = screenWidth > 0f ? screenWidth : 1f;
        this.screenHeight = screenHeight > 0f ? screenHeight : 1f;
    }

    /// <summary>Resets every bin to "no mesh recorded yet" before a new frame.</summary>
    public void Reset()
    {
        Array.Fill(depths, NoDepth);
    }

    /// <summary>
    /// Records a mesh vertex's NDC depth at its screen position. Only the minimum
    /// depth per bin is kept (closest visible mesh).
    /// </summary>
    public void Write(float screenX, float screenY, float depth)
    {
        if (!float.IsFinite(screenX) || !float.IsFinite(screenY) || !float.IsFinite(depth))
        {
            return;
        }

        if (!TryGetBinIndex(screenX, screenY, out int index))
        {
            return;
        }

        if (depth < depths[index])
        {
            depths[index] = depth;
        }
    }

    /// <summary>
    /// True if the supplied vertex at <paramref name="depth"/> sits behind the
    /// recorded mesh depth at its screen position (and therefore should be culled).
    /// Off-screen points return false so the caller keeps its own clipping logic.
    /// </summary>
    public bool IsBehind(float screenX, float screenY, float depth, float epsilon = 0.005f)
    {
        if (!float.IsFinite(screenX) || !float.IsFinite(screenY) || !float.IsFinite(depth))
        {
            return false;
        }
        if (!TryGetBinIndex(screenX, screenY, out int index))
        {
            return false;
        }

        float meshDepth = depths[index];
        if (meshDepth == NoDepth)
        {
            return false;
        }

        return depth > meshDepth + epsilon;
    }

    /// <summary>Convenience overload accepting a nullable projected screen point.</summary>
    public bool IsBehind(Vector3? screen, float epsilon = 0.005f)
    {
        if (screen is not { } v)
        {
            return false;
        }
        return IsBehind(v.X, v.Y, v.Z, epsilon);
    }

    private bool TryGetBinIndex(float screenX, float screenY, out int index)
    {
        if (screenX < 0f || screenY < 0f || screenX >= screenWidth || screenY >= screenHeight)
        {
            index = -1;
            return false;
        }

        int bx = (int)(screenX / screenWidth * binsX);
        int by = (int)(screenY / screenHeight * binsY);
        if (bx >= binsX) bx = binsX - 1;
        if (by >= binsY) by = binsY - 1;
        index = (by * binsX) + bx;
        return true;
    }
}