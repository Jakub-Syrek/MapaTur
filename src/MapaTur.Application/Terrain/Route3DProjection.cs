using System.Numerics;
using MapaTur.Domain.Routing;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Projects a planned <see cref="Route"/> polyline onto the 3D viewport. Each vertex
/// is lifted to the underlying DEM elevation (plus a small bias so the line sits
/// above the terrain) and run through the camera's view+projection pipeline.
/// </summary>
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
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(raster);
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(camera);

        Matrix4x4 viewProjection = (screenWidth > 0f && screenHeight > 0f)
            ? camera.BuildViewProjection(screenWidth / screenHeight)
            : Matrix4x4.Identity;

        var polyline = route.ToPolyline();
        var points = new Vector3?[polyline.Count];
        for (int i = 0; i < polyline.Count; i++)
        {
            var geo = polyline[i];
            float groundElevation = (float)raster.SampleBilinear(geo.Longitude, geo.Latitude);
            float liftedElevation = groundElevation + routeLiftMeters;
            Vector3 world = mesh.GeoToWorld(geo, liftedElevation);
            points[i] = camera.ProjectToScreen(world, viewProjection, screenWidth, screenHeight);
        }

        return new ProjectedRoute(route, points);
    }
}
