using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Projects detected <see cref="TerrainPeak"/> summits onto the 3D viewport. A summit already
/// carries its own elevation (it is a DEM cell), so — unlike <see cref="Climbing3DProjection"/> —
/// no raster lookup is needed; the marker is lifted slightly above the surface so its label clears
/// the terrain instead of z-fighting into it.
/// </summary>
public static class Peak3DProjection
{
    /// <summary>
    /// Projects every summit in <paramref name="peaks"/> to screen space.
    /// </summary>
    /// <param name="peaks">Summits to project (typically <see cref="PeakDetector.Detect"/> output).</param>
    /// <param name="mesh">Mesh whose world-space convention defines the coordinate system.</param>
    /// <param name="camera">Camera providing the view + projection matrices.</param>
    /// <param name="screenWidth">Viewport width in pixels.</param>
    /// <param name="screenHeight">Viewport height in pixels.</param>
    /// <param name="markerLiftMeters">Vertical offset above the summit so the marker/label sits clear of the ground.</param>
    public static IReadOnlyList<ProjectedPeak> Project(
        IReadOnlyList<TerrainPeak> peaks,
        TerrainMesh3D mesh,
        Camera3D camera,
        float screenWidth,
        float screenHeight,
        float markerLiftMeters = 40f)
    {
        ArgumentNullException.ThrowIfNull(peaks);
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(camera);

        if (peaks.Count == 0)
        {
            return Array.Empty<ProjectedPeak>();
        }

        Matrix4x4 viewProjection = (screenWidth > 0f && screenHeight > 0f)
            ? camera.BuildViewProjection(screenWidth / screenHeight)
            : Matrix4x4.Identity;

        IReadOnlyList<MarkerWorldPoint<TerrainPeak>> world = ToWorld(peaks, mesh, markerLiftMeters);
        var result = new List<ProjectedPeak>(world.Count);
        foreach (MarkerWorldPoint<TerrainPeak> marker in world)
        {
            Vector3? screen = camera.ProjectToScreen(marker.World, viewProjection, screenWidth, screenHeight);
            result.Add(new ProjectedPeak(marker.Source, screen));
        }

        return result;
    }

    /// <summary>
    /// Camera-independent stage: lifts every summit to its own elevation (no raster lookup) and converts
    /// it into mesh world space. Compute once and reuse across frames — see
    /// <see cref="Marker3DOverlayProjector{TSource, TProjected}"/>.
    /// </summary>
    /// <param name="peaks">Summits to convert.</param>
    /// <param name="mesh">Mesh whose world-space convention defines the coordinate system.</param>
    /// <param name="markerLiftMeters">Vertical offset above the summit so the marker/label sits clear of the ground.</param>
    public static IReadOnlyList<MarkerWorldPoint<TerrainPeak>> ToWorld(
        IReadOnlyList<TerrainPeak> peaks,
        TerrainMesh3D mesh,
        float markerLiftMeters = 40f)
    {
        ArgumentNullException.ThrowIfNull(peaks);
        ArgumentNullException.ThrowIfNull(mesh);

        var result = new List<MarkerWorldPoint<TerrainPeak>>(peaks.Count);
        foreach (var peak in peaks)
        {
            float liftedElevation = (float)peak.ElevationMeters + markerLiftMeters;
            Vector3 world = mesh.GeoToWorld(peak.Location, liftedElevation);
            result.Add(new MarkerWorldPoint<TerrainPeak>(peak, world));
        }

        return result;
    }
}