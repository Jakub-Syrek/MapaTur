namespace MapaTur.Application.Terrain;

/// <summary>
/// Computes near/far clip planes that hug the scene around the camera target instead of using a
/// fixed, wildly oversized range. A huge near/far ratio (the old 10 m … 1 000 000 m default) crushes
/// almost all of the terrain into a sliver of NDC depth, wrecking the painter's-algorithm sort and
/// letting close geometry punch through the near plane. Fitting the planes to the camera distance and
/// scene size keeps depth precision high and avoids near-plane clipping holes when zoomed in.
/// </summary>
public static class CameraClipPlanes
{
    // Pull the near plane 10% closer than the nearest scene point and push the far plane 10% past the
    // farthest, so rounding / a vertically-panned target never clips visible geometry.
    private const float Margin = 0.1f;
    private const float MinNear = 1f;

    /// <summary>
    /// Returns (near, far) for a camera <paramref name="distance"/> from its target, where the scene
    /// is bounded by a sphere of radius <paramref name="sceneRadius"/> around that target.
    /// </summary>
    /// <param name="distance">Camera distance from the target, in metres.</param>
    /// <param name="sceneRadius">Radius of a sphere enclosing the renderable scene, in metres.</param>
    public static (float Near, float Far) Fit(float distance, float sceneRadius)
    {
        float radius = MathF.Max(0f, sceneRadius);
        float near = MathF.Max(MinNear, (distance - radius) * (1f - Margin));
        float far = MathF.Max(near + 1f, (distance + radius) * (1f + Margin));
        return (near, far);
    }
}