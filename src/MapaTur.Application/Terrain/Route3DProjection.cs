using MapaTur.Domain.Routing;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Projects a planned <see cref="Route"/> polyline onto the 3D viewport. Each vertex
/// is lifted to the underlying DEM elevation (plus a small bias so the line sits
/// above the terrain) and run through the camera's view+projection pipeline.
/// </summary>
/// <remarks>
/// This is the eager, single-call wrapper around <see cref="Route3DWorldProjection"/>: it runs the
/// camera-independent world stage and the per-frame screen stage back-to-back. Hosts that re-render
/// every frame during a gesture should instead cache <see cref="Route3DWorldProjection.ToWorld"/>
/// once and call <see cref="Route3DWorldProjection.ToScreen"/> per frame.
/// </remarks>
public static class Route3DProjection
{
    /// <summary>
    /// Projects <paramref name="route"/> to screen space.
    /// </summary>
    /// <param name="route">Route to project.</param>
    /// <param name="raster">Source DEM used to look up elevations along the route.</param>
    /// <param name="mesh">Mesh whose world-space convention defines the coordinate system.</param>
    /// <param name="camera">Camera providing view + projection matrices.</param>
    /// <param name="screenWidth">Viewport width in pixels.</param>
    /// <param name="screenHeight">Viewport height in pixels.</param>
    /// <param name="routeLiftMeters">Vertical offset added to each vertex before exaggeration so the route sits above the mesh surface. Defaults slightly higher than trails so the route line wins z-fights at shared waypoints.</param>
    public static ProjectedRoute Project(
        Route route,
        DemRaster raster,
        TerrainMesh3D mesh,
        Camera3D camera,
        float screenWidth,
        float screenHeight,
        float routeLiftMeters = 8f)
    {
        // camera null is validated by ToScreen; route/raster/mesh by ToWorld.
        var world = Route3DWorldProjection.ToWorld(route, raster, mesh, routeLiftMeters);
        return Route3DWorldProjection.ToScreen(world, camera, screenWidth, screenHeight);
    }
}