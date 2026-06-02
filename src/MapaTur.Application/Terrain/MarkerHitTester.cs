using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Picks the marker a screen tap selects. Given the per-marker projected screen positions
/// (pixel X/Y + NDC depth Z, as produced by the 3D marker projectors) and a tap point, it
/// returns the index of the marker the user most likely meant to hit.
/// </summary>
public static class MarkerHitTester
{
    /// <summary>
    /// Returns the index of the marker nearest the tap among those whose screen position is within
    /// <paramref name="radiusPx"/> pixels of (<paramref name="tapX"/>, <paramref name="tapY"/>).
    /// When several overlap within the radius the front-most one (smallest NDC depth <c>Z</c>) wins,
    /// so a tap selects the marker drawn on top; ties on depth break toward the marker closest to the
    /// tap. Markers with a null screen position (off-frustum) are ignored. Returns <c>null</c> when
    /// nothing is in range.
    /// </summary>
    /// <param name="screenPositions">Projected screen positions; null entries are off-frustum.</param>
    /// <param name="tapX">Tap X in pixels.</param>
    /// <param name="tapY">Tap Y in pixels.</param>
    /// <param name="radiusPx">Hit radius in pixels (inclusive). Must be non-negative.</param>
    public static int? HitTest(IReadOnlyList<Vector3?> screenPositions, float tapX, float tapY, float radiusPx)
    {
        ArgumentNullException.ThrowIfNull(screenPositions);
        if (radiusPx < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(radiusPx), "Hit radius must be non-negative.");
        }

        float radiusSq = radiusPx * radiusPx;
        int? best = null;
        float bestDepth = float.PositiveInfinity;
        float bestDistSq = float.PositiveInfinity;

        for (int i = 0; i < screenPositions.Count; i++)
        {
            if (screenPositions[i] is not { } pos)
            {
                continue;
            }

            float dx = pos.X - tapX;
            float dy = pos.Y - tapY;
            float distSq = (dx * dx) + (dy * dy);
            if (distSq > radiusSq)
            {
                continue;
            }

            // Front-most wins (smaller depth = closer to camera = drawn on top). On a depth tie the
            // marker nearer the tap point wins.
            if (best is null || pos.Z < bestDepth || (pos.Z == bestDepth && distSq < bestDistSq))
            {
                best = i;
                bestDepth = pos.Z;
                bestDistSq = distSq;
            }
        }

        return best;
    }
}