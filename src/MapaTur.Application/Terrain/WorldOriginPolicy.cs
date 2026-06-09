using System;
using System.Numerics;

using MapaTur.Domain.Geography;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Outcome of <see cref="WorldOriginPolicy.Evaluate"/>: whether the streamed scene should re-anchor its world
/// origin to the camera, the origin to use going forward, and the world-space translation to add to geometry
/// that was already built about the previous origin so it stays put in the new frame.
/// </summary>
/// <param name="ShouldReanchor"><c>true</c> when the camera drifted past the threshold and the origin moved.</param>
/// <param name="Origin">The origin to build/keep geometry about (unchanged when not re-anchoring).</param>
/// <param name="ExistingShift">Translation to apply to already-built geometry; <see cref="Vector3.Zero"/> when not re-anchoring.</param>
public readonly record struct WorldOriginDecision(bool ShouldReanchor, GeoPoint Origin, Vector3 ExistingShift);

/// <summary>
/// Decides when a streamed terrain scene must re-anchor its float world origin to the camera. Keeping the origin
/// near the camera preserves float precision — a distant origin is exactly what made the abandoned moving-window
/// approach jitter/teleport (see docs/HANDOFF-1m-coverage.md §6). Pure logic: no rendering, no camera mutation.
/// </summary>
public static class WorldOriginPolicy
{
    /// <summary>
    /// Evaluates the camera's planar distance from the current origin (engine local-tangent metric). When it
    /// exceeds <paramref name="reanchorThresholdMeters"/> the scene re-anchors to <paramref name="cameraFocus"/>
    /// and the returned <see cref="WorldOriginDecision.ExistingShift"/> is the world position of the OLD origin
    /// in the NEW frame — add it to every already-built vertex to keep that geometry in place without rebuilding.
    /// At or within the threshold nothing moves.
    /// </summary>
    public static WorldOriginDecision Evaluate(GeoPoint currentOrigin, GeoPoint cameraFocus, double reanchorThresholdMeters)
    {
        Vector3 cameraInFrame = LocalTangentProjection.GeoToWorld(cameraFocus, 0f, currentOrigin, 1f);
        double distance = Math.Sqrt((cameraInFrame.X * cameraInFrame.X) + (cameraInFrame.Y * cameraInFrame.Y));

        if (distance <= reanchorThresholdMeters)
        {
            return new WorldOriginDecision(false, currentOrigin, Vector3.Zero);
        }

        // Re-anchor to the camera. Geometry built about the old origin shifts by the old origin's position in the
        // new frame so a fixed point keeps (approximately — local-tangent, a few metres at regional scale) its
        // world coordinates; Z is unaffected (the origin only moves in XY).
        Vector3 existingShift = LocalTangentProjection.GeoToWorld(currentOrigin, 0f, cameraFocus, 1f);
        return new WorldOriginDecision(true, cameraFocus, existingShift);
    }
}