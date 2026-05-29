using MapaTur.Domain.Terrain;
using MapaTur.Domain.Trails;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Projects <see cref="Trail"/> polylines onto the 3D viewport. Each trail vertex
/// is lifted to the underlying DEM elevation (plus a small bias so the line sits
/// above the terrain) and run through the camera's view+projection pipeline.
/// </summary>
/// <remarks>
/// This is the eager, single-call wrapper around <see cref="Trail3DWorldProjection"/>: it runs the
/// camera-independent world stage and the per-frame screen stage back-to-back. Hosts that re-render
/// every frame during a gesture should instead cache <see cref="Trail3DWorldProjection.ToWorld"/>
/// once and call <see cref="Trail3DWorldProjection.ToScreen"/> per frame — the world stage does the
/// expensive DEM sampling + geo→world maths that don't change while only the camera orbits.
/// </remarks>
public static class Trail3DProjection
{
    /// <summary>
    /// Projects every trail in <paramref name="trails"/> to screen space.
    /// </summary>
    /// <param name="trails">Trails to project.</param>
    /// <param name="raster">Source DEM used to look up elevations along each trail.</param>
    /// <param name="mesh">Mesh whose world-space convention defines the coordinate system.</param>
    /// <param name="camera">Camera providing view + projection matrices.</param>
    /// <param name="screenWidth">Viewport width in pixels.</param>
    /// <param name="screenHeight">Viewport height in pixels.</param>
    /// <param name="trailLiftMeters">Vertical offset added to each vertex before exaggeration so trails sit above the mesh surface; tune to taste.</param>
    public static IReadOnlyList<ProjectedTrail> Project(
        IReadOnlyList<Trail> trails,
        DemRaster raster,
        TerrainMesh3D mesh,
        Camera3D camera,
        float screenWidth,
        float screenHeight,
        float trailLiftMeters = 5f)
    {
        // camera null is validated by ToScreen; trails/raster/mesh by ToWorld.
        var world = Trail3DWorldProjection.ToWorld(trails, raster, mesh, trailLiftMeters);
        return Trail3DWorldProjection.ToScreen(world, camera, screenWidth, screenHeight);
    }
}