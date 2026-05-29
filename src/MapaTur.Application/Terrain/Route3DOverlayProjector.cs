using System.Numerics;

using MapaTur.Domain.Routing;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Stateful, per-view route projector that owns the camera-independent world cache from
/// <see cref="Route3DWorldProjection.ToWorld"/> and a single reusable screen buffer, so a host that
/// re-renders every frame during a gesture allocates nothing per frame.
/// <para>
/// The world cache (DEM elevation sampling + geo→world conversion) is rebuilt only when the route,
/// raster, mesh or lift change by reference/value. Every frame <see cref="Project"/> rebuilds the
/// view+projection once and fills the cached buffer in place, then returns a <see cref="ProjectedRoute"/>
/// (a struct, so by-value) wrapping the same backing array — values change, the reference doesn't.
/// </para>
/// Hold one instance per <c>Terrain3DView</c>. It is not thread-safe; call <see cref="Project"/> from
/// the render thread only.
/// </summary>
public sealed class Route3DOverlayProjector
{
    private RouteWorldLine worldCache;
    private bool hasWorld;
    private Route? cachedRoute;
    private DemRaster? cachedRaster;
    private TerrainMesh3D? cachedMesh;
    private float cachedLift;

    private Vector3?[]? screenBuffer;

    /// <summary>
    /// Projects <paramref name="route"/> to screen space, reusing the cached world points and screen
    /// buffer whenever the inputs are unchanged since the last call.
    /// </summary>
    /// <param name="route">Route to project.</param>
    /// <param name="raster">Source DEM used to look up elevations along the route.</param>
    /// <param name="mesh">Mesh whose world-space convention defines the coordinate system.</param>
    /// <param name="camera">Camera providing view + projection matrices.</param>
    /// <param name="screenWidth">Viewport width in pixels.</param>
    /// <param name="screenHeight">Viewport height in pixels.</param>
    /// <param name="routeLiftMeters">Vertical offset added to each vertex before exaggeration so the route sits above the mesh surface.</param>
    public ProjectedRoute Project(
        Route route,
        DemRaster raster,
        TerrainMesh3D mesh,
        Camera3D camera,
        float screenWidth,
        float screenHeight,
        float routeLiftMeters = 8f)
    {
        ArgumentNullException.ThrowIfNull(camera);

        if (!hasWorld
            || !ReferenceEquals(cachedRoute, route)
            || !ReferenceEquals(cachedRaster, raster)
            || !ReferenceEquals(cachedMesh, mesh)
            || cachedLift != routeLiftMeters)
        {
            // ToWorld validates route/raster/mesh.
            worldCache = Route3DWorldProjection.ToWorld(route, raster, mesh, routeLiftMeters);
            cachedRoute = route;
            cachedRaster = raster;
            cachedMesh = mesh;
            cachedLift = routeLiftMeters;
            hasWorld = true;
            screenBuffer = new Vector3?[worldCache.World.Count];
        }

        Matrix4x4 viewProjection = (screenWidth > 0f && screenHeight > 0f)
            ? camera.BuildViewProjection(screenWidth / screenHeight)
            : Matrix4x4.Identity;

        var world = worldCache.World;
        var buffer = screenBuffer!;
        for (int i = 0; i < world.Count; i++)
        {
            buffer[i] = camera.ProjectToScreen(world[i], viewProjection, screenWidth, screenHeight);
        }

        return new ProjectedRoute(worldCache.Source, buffer);
    }
}