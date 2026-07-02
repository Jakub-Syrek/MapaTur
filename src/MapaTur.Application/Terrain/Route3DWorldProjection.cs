using System.Numerics;

using MapaTur.Domain.Geography;
using MapaTur.Domain.Routing;
using MapaTur.Domain.Terrain;
using MapaTur.Domain.Trails;

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
    /// <summary>Max spacing (m) between seated route vertices — sparse segments are subdivided to this so the line
    /// hugs the 1 m terrain. 5 m (down from 12) shortens the chord so the route no longer lifts off the fine
    /// detail over bumps/dips; recomputed only on a detail reload, so the extra points are off the per-frame path.</summary>
    private const double DensifySpacingMeters = 5.0;

    /// <summary>
    /// Lifts every route vertex to its DEM elevation and converts it into mesh world space.
    /// Camera-independent — compute once and reuse across frames.
    /// </summary>
    /// <param name="route">Route to convert.</param>
    /// <param name="raster">Source DEM used to look up elevations along the route.</param>
    /// <param name="mesh">Mesh whose world-space convention defines the coordinate system.</param>
    /// <param name="routeLiftMeters">Vertical offset added to each vertex (before exaggeration) so the route sits above the mesh surface. Defaults slightly higher than trails so the route wins z-fights at shared waypoints.</param>
    /// <param name="detail">Optional 1 m LOD detail field: inside its window the vertex seats on the detail
    /// elevation (matching the rendered near-field mesh) instead of the coarse base. See <see cref="Trail3DWorldProjection.ToWorld"/>.</param>
    /// <param name="followTrails">Optional rendered trails: when supplied, the route is re-laid onto the actual
    /// trail vertices it traverses (<see cref="RouteTrailConflation"/>) so it lies on its trail instead of the
    /// routing graph's snapped node coords. Does not change the planned path — only the rendered geometry.</param>
    /// <param name="bakedIndex">Optional baked-tile index — see <see cref="Trail3DWorldProjection.ToWorld"/>'s
    /// matching parameter for why this matters (the same real-vs-static-base elevation mismatch applies here).</param>
    public static RouteWorldLine ToWorld(
        Route route,
        DemRaster raster,
        TerrainMesh3D mesh,
        float routeLiftMeters = 8f,
        DetailElevationField? detail = null,
        IReadOnlyList<Trail>? followTrails = null,
        BakedTileAvailabilityIndex? bakedIndex = null)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(raster);
        ArgumentNullException.ThrowIfNull(mesh);

        // Re-lay the route onto the trail geometry it follows (so it doesn't render beside its trail), then
        // densify so it hugs the 1 m terrain (see Trail3DWorldProjection) instead of cutting straight across
        // the relief between sparse waypoints.
        IReadOnlyList<GeoPoint> source = followTrails is { Count: > 0 }
            ? RouteTrailConflation.Conflate(route.ToPolyline(), followTrails)
            : route.ToPolyline();
        var polyline = GeoPolylineDensifier.Densify(source, DensifySpacingMeters);
        var world = new Vector3[polyline.Count];
        var bakedTileCache = new Dictionary<(int Zoom, int X, int Y), DemRaster?>();
        // True-metric lift: divide by the exaggeration so GeoToWorld's Z scaling leaves the route a real
        // routeLiftMeters above the surface (a raw lift scaled with Pion and made the line float).
        float liftElevation = mesh.VerticalExaggeration > 0f ? routeLiftMeters / mesh.VerticalExaggeration : routeLiftMeters;
        for (int i = 0; i < polyline.Count; i++)
        {
            var geo = polyline[i];
            double ground;
            if (bakedIndex is not null && Trail3DWorldProjection.TryGetBakedElevation(bakedIndex, bakedTileCache, geo, out double bakedElevation))
            {
                ground = bakedElevation;
            }
            else
            {
                ground = detail is not null && detail.TryGetElevation(geo.Longitude, geo.Latitude, out double detailElevation)
                    ? detailElevation
                    : raster.SampleBilinear(geo.Longitude, geo.Latitude);
            }

            world[i] = mesh.GeoToWorld(geo, (float)ground + liftElevation);
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