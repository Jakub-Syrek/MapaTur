using System.Numerics;

using MapaTur.Domain.Routing;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Two-stage route projection that splits the camera-independent work from the per-frame work,
/// mirroring <see cref="Trail3DWorldProjection"/>.
/// <para>
/// <see cref="ToWorld"/> lifts each polyline vertex to its DEM elevation and converts it into mesh
/// world space — work that depends only on the route, raster and mesh. <see cref="ToScreen"/> runs
/// the camera view+projection transform, the only part that changes while the camera orbits.
/// </para>
/// <see cref="Route3DProjection.Project"/> is the eager wrapper that runs both stages.
/// </summary>
public static class Route3DWorldProjection
{
    /// <summary>
    /// Lifts every route vertex to its DEM elevation and converts it into mesh world space.
    /// Camera-independent — compute once and reuse across frames.
    /// </summary>
    /// <param name="route">Route to convert.</param>
    /// <param name="raster">Source DEM used to look up elevations along the route.</param>
    /// <param name="mesh">Mesh whose world-space convention defines the coordinate system.</param>
    /// <param name="routeLiftMeters">Vertical offset added to each vertex (before exaggeration) so the route sits above the mesh surface. Defaults slightly higher than trails so the route wins z-fights at shared waypoints.</param>
    public static RouteWorldLine ToWorld(
        Route route,
        DemRaster raster,
        TerrainMesh3D mesh,
        float routeLiftMeters = 8f)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(raster);
        ArgumentNullException.ThrowIfNull(mesh);

        var polyline = route.ToPolyline();
        var world = new Vector3[polyline.Count];
        for (int i = 0; i < polyline.Count; i++)
        {
            var geo = polyline[i];
            float groundElevation = (float)raster.SampleBilinear(geo.Longitude, geo.Latitude);
            world[i] = mesh.GeoToWorld(geo, groundElevation + routeLiftMeters);
        }

        return new RouteWorldLine(route, world);
    }

    /// <summary>
    /// Projects a pre-computed <see cref="RouteWorldLine"/> to screen space through the camera. This
    /// is the only per-frame stage; a vertex behind the camera or outside the clip range projects to
    /// null.
    /// </summary>
    /// <param name="worldLine">World-space route from <see cref="ToWorld"/>.</param>
    /// <param name="camera">Camera providing view + projection matrices.</param>
    /// <param name="screenWidth">Viewport width in pixels.</param>
    /// <param name="screenHeight">Viewport height in pixels.</param>
    public static ProjectedRoute ToScreen(
        RouteWorldLine worldLine,
        Camera3D camera,
        float screenWidth,
        float screenHeight)
    {
        ArgumentNullException.ThrowIfNull(camera);

        Matrix4x4 viewProjection = (screenWidth > 0f && screenHeight > 0f)
            ? camera.BuildViewProjection(screenWidth / screenHeight)
            : Matrix4x4.Identity;

        var world = worldLine.World;
        var points = new Vector3?[world.Count];
        for (int i = 0; i < world.Count; i++)
        {
            points[i] = camera.ProjectToScreen(world[i], viewProjection, screenWidth, screenHeight);
        }

        return new ProjectedRoute(worldLine.Source, points);
    }
}