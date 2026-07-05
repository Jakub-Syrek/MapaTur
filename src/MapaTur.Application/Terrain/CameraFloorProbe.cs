using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// The world-XY points the camera's anti-tunnelling floor samples each frame. One sample DIRECTLY under
/// the eye misses the slope the camera is flying TOWARD — the near plane pierces a wall before the eye is
/// over it — so the probe set is: the eye, one look-ahead point along the horizontal view direction, and a
/// small ring around the eye (covers strafing and the near-plane's width). The floor then rises to the
/// HIGHEST sampled surface, lifting the camera before any of those points is breached. Pure geometry.
/// </summary>
public static class CameraFloorProbe
{
    /// <summary>
    /// Builds the probe points. Index 0 is always the eye itself.
    /// </summary>
    /// <param name="eyeXY">Camera eye position, world XY (metres).</param>
    /// <param name="viewDirXY">Horizontal component of the view direction; any length, may be zero
    /// (straight-down view) — then the ahead probe degenerates to the eye.</param>
    /// <param name="aheadMeters">How far ahead of the eye to probe along the view direction.</param>
    /// <param name="ringRadiusMeters">Radius of the probe ring around the eye.</param>
    /// <param name="ringCount">Points on the ring (default 4 — the axis-diagonal compromise).</param>
    /// <returns>Probe points, eye first; 1 + 1 + <paramref name="ringCount"/> entries.</returns>
    public static IReadOnlyList<Vector2> ProbePoints(
        Vector2 eyeXY, Vector2 viewDirXY, float aheadMeters, float ringRadiusMeters, int ringCount = 4)
    {
        var points = new List<Vector2>(2 + Math.Max(0, ringCount)) { eyeXY };

        float dirLength = viewDirXY.Length();
        Vector2 ahead = dirLength > 1e-3f
            ? eyeXY + (viewDirXY * (aheadMeters / dirLength))
            : eyeXY; // straight-down view: no horizontal motion to anticipate
        points.Add(ahead);

        for (int i = 0; i < ringCount; i++)
        {
            float angle = (MathF.Tau * i) / ringCount;
            points.Add(eyeXY + (new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * ringRadiusMeters));
        }

        return points;
    }
}