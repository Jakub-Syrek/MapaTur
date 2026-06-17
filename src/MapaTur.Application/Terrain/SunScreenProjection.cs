using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Projects the directional sun into the post-process texture's UV space for the screen-space god-ray
/// (crepuscular-ray) pass. The radial blur marches each pixel toward the sun's on-screen position, so it
/// needs that position in the same convention the fullscreen post passes use (UV origin bottom-left,
/// <c>uv = ndc * 0.5 + 0.5</c>), plus a visibility flag so the renderer skips the pass when the sun is
/// behind the camera or well off the frame. The sun is treated as a point far along its direction; only the
/// behind-camera case is culled (not the far plane), so a directional light always projects when in front.
/// </summary>
public static class SunScreenProjection
{
    // Far enough along the sun direction to read as a directional light at infinity, finite to keep the
    // homogeneous transform well-conditioned.
    private const float SunDistanceMeters = 1_000_000f;

    // Rays still make sense when the sun is just past the frame edge (they stream inward), so allow a
    // margin around [0,1] before declaring the sun off-screen.
    private const float FrameMargin = 0.25f;

    /// <summary>
    /// Returns whether the god-ray pass should draw and, if so, the sun's UV position (bottom-left origin,
    /// matching the post passes).
    /// </summary>
    public static (bool Visible, Vector2 Uv) Project(Camera3D camera, Vector3 sunDirection, float screenWidth, float screenHeight)
    {
        ArgumentNullException.ThrowIfNull(camera);
        if (screenWidth <= 0f || screenHeight <= 0f || sunDirection.LengthSquared() < 1e-8f)
        {
            return (false, default);
        }

        Vector3 sunDir = Vector3.Normalize(sunDirection);
        Vector3 sunPoint = camera.Position + (sunDir * SunDistanceMeters);

        Matrix4x4 viewProjection = camera.BuildViewProjection(screenWidth / screenHeight);
        Vector4 clip = Vector4.Transform(new Vector4(sunPoint, 1f), viewProjection);
        if (clip.W <= 0f)
        {
            return (false, default); // sun is behind the camera
        }

        float ndcX = clip.X / clip.W;
        float ndcY = clip.Y / clip.W;
        var uv = new Vector2((ndcX * 0.5f) + 0.5f, (ndcY * 0.5f) + 0.5f);

        bool visible = uv.X >= -FrameMargin && uv.X <= 1f + FrameMargin
            && uv.Y >= -FrameMargin && uv.Y <= 1f + FrameMargin;
        return (visible, uv);
    }
}