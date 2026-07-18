using System.Numerics;

using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Tests whether a label marker (a named summit / POI sitting on the terrain) is visible from the
/// camera, or hidden behind a ridge. The GL terrain is depth-buffered, but the peak/POI labels are
/// drawn by Skia ON TOP of the composited terrain image with no depth test, so without this they
/// "punch through" ridges. We cast the camera→marker ray at the DEM: a surface hit that lands well
/// short of the marker means a ridge stands in the way → the label is hidden.
/// </summary>
public static class TerrainOcclusion
{
    // March step for the occlusion ray. Coarser than the look-at ray (this only needs to catch ridges,
    // not resolve a sub-metre look-at), so dozens of markers can be tested per frame cheaply.
    private const float StepMeters = 40f;

    // The marker sits ON the terrain, so the ray naturally hits the surface AT the marker. A hit within
    // this margin of the marker distance is the marker's own ground, not an occluder.
    private const float SelfHitMarginMeters = 120f;

    /// <summary>
    /// True when <paramref name="markerWorld"/> is visible from <paramref name="cameraWorld"/> — i.e. no
    /// DEM ridge blocks the straight line between them.
    /// </summary>
    /// <param name="cameraWorld">Camera eye position in the metric world frame.</param>
    /// <param name="markerWorld">Marker position (on the terrain) in the same frame.</param>
    /// <param name="raster">DEM heightfield to test against.</param>
    /// <param name="anchor">Shared projection origin tying the world frame to geographic coordinates.</param>
    /// <param name="verticalExaggeration">Z scale applied to elevations, matching the rendered terrain.</param>
    public static bool IsVisible(
        Vector3 cameraWorld,
        Vector3 markerWorld,
        DemRaster raster,
        GeoPoint anchor,
        float verticalExaggeration)
    {
        ArgumentNullException.ThrowIfNull(raster);

        Vector3 toMarker = markerWorld - cameraWorld;
        float distance = toMarker.Length();
        if (distance < 1f)
        {
            return true;
        }

        var ray = new Ray(cameraWorld, toMarker / distance);
        Vector3? hit = TerrainRaycaster.Intersect(ray, raster, anchor, verticalExaggeration, distance, StepMeters);
        if (hit is not { } surface)
        {
            return true; // nothing between the eye and the marker
        }

        float hitDistance = Vector3.Distance(cameraWorld, surface);
        // Visible when the first surface the ray meets is the marker's OWN ground (within the margin);
        // occluded when a ridge is hit clearly before the marker.
        return hitDistance >= distance - SelfHitMarginMeters;
    }

    // Self-hit margin for the FINE march: the detailed surface is sharp, so the marker's own rock is met
    // within a much tighter band than the coarse raster's 120 m.
    private const float SelfHitMarginFineMeters = 25f;

    /// <summary>
    /// As <see cref="IsVisible"/>, but marches the ray against the SAME detailed surface the terrain is
    /// rendered from (via <paramref name="sampleGround"/>: fine 1 m detail where streamed, coarse base
    /// otherwise), instead of the coarse raster. On sharp towers (Mnich needle) the coarse DEM sits tens
    /// of metres below the rendered rock, so a coarse raycast lets labels "punch through" the detailed
    /// wall; this catches them. Steps finely in the near field (the detailed rock in front of the camera)
    /// and coarsens far out where only big ridges matter. <paramref name="sampleGround"/> returns RAW
    /// elevation in metres (Z scaling is applied here).
    /// </summary>
    public static bool IsVisibleFine(
        Vector3 cameraWorld, Vector3 markerWorld, Func<Vector2, float?> sampleGround, float verticalExaggeration)
    {
        ArgumentNullException.ThrowIfNull(sampleGround);

        Vector3 toMarker = markerWorld - cameraWorld;
        float distance = toMarker.Length();
        if (distance < 1f)
        {
            return true;
        }

        Vector3 dir = toMarker / distance;
        float limit = distance - SelfHitMarginFineMeters;
        for (float t = 6f; t < limit;)
        {
            Vector3 p = cameraWorld + (dir * t);
            if (sampleGround(new Vector2(p.X, p.Y)) is { } ground && p.Z < (ground * verticalExaggeration) - 1f)
            {
                return false; // the ray dips below the rendered surface before reaching the marker → occluded
            }

            t += t < 800f ? 6f : 30f; // fine near (detailed rock), coarse far (ridges only)
        }

        return true;
    }
}