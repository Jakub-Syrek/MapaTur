using System.Numerics;

using MapaTur.Domain.Climbing;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Projects <see cref="ClimbingArea"/> points onto the 3D viewport. Each marker is
/// lifted above the underlying DEM elevation so it visibly sits above the ground
/// instead of being z-fought into the mesh.
/// </summary>
public static class Climbing3DProjection
{
    /// <summary>
    /// Projects every climbing area in <paramref name="areas"/> whose lon/lat falls
    /// inside the raster's bounding box. Areas entirely outside the DEM are dropped —
    /// they have no defined elevation and would render at the wrong position anyway.
    /// </summary>
    /// <param name="areas">Climbing areas to project.</param>
    /// <param name="raster">Source DEM used to look up ground elevation.</param>
    /// <param name="mesh">Mesh whose world-space convention defines the coordinate system.</param>
    /// <param name="camera">Camera providing view + projection matrices.</param>
    /// <param name="screenWidth">Viewport width in pixels.</param>
    /// <param name="screenHeight">Viewport height in pixels.</param>
    /// <param name="markerLiftMeters">Vertical offset above the DEM surface so the marker sits clear of the ground.</param>
    public static IReadOnlyList<ProjectedClimbingArea> Project(
        IReadOnlyList<ClimbingArea> areas,
        DemRaster raster,
        TerrainMesh3D mesh,
        Camera3D camera,
        float screenWidth,
        float screenHeight,
        float markerLiftMeters = 30f)
    {
        ArgumentNullException.ThrowIfNull(areas);
        ArgumentNullException.ThrowIfNull(raster);
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(camera);

        if (areas.Count == 0)
        {
            return Array.Empty<ProjectedClimbingArea>();
        }

        Matrix4x4 viewProjection = (screenWidth > 0f && screenHeight > 0f)
            ? camera.BuildViewProjection(screenWidth / screenHeight)
            : Matrix4x4.Identity;

        IReadOnlyList<MarkerWorldPoint<ClimbingArea>> world = ToWorld(areas, raster, mesh, markerLiftMeters);
        var result = new List<ProjectedClimbingArea>(world.Count);
        foreach (MarkerWorldPoint<ClimbingArea> marker in world)
        {
            Vector3? screen = camera.ProjectToScreen(marker.World, viewProjection, screenWidth, screenHeight);
            result.Add(new ProjectedClimbingArea(marker.Source, screen));
        }

        return result;
    }

    /// <summary>
    /// Camera-independent stage: lifts every in-bbox climbing area to its DEM elevation and converts it
    /// into mesh world space. Areas whose lon/lat falls outside the raster are dropped (no defined
    /// elevation). Compute once and reuse across frames — see <see cref="Marker3DOverlayProjector{TSource, TProjected}"/>.
    /// </summary>
    /// <param name="areas">Climbing areas to convert.</param>
    /// <param name="raster">Source DEM used to look up ground elevation.</param>
    /// <param name="mesh">Mesh whose world-space convention defines the coordinate system.</param>
    /// <param name="markerLiftMeters">Vertical offset above the DEM surface so the marker sits clear of the ground.</param>
    public static IReadOnlyList<MarkerWorldPoint<ClimbingArea>> ToWorld(
        IReadOnlyList<ClimbingArea> areas,
        DemRaster raster,
        TerrainMesh3D mesh,
        float markerLiftMeters = 30f)
    {
        ArgumentNullException.ThrowIfNull(areas);
        ArgumentNullException.ThrowIfNull(raster);
        ArgumentNullException.ThrowIfNull(mesh);

        double demWest = raster.West;
        double demEast = raster.East;
        double demSouth = raster.South;
        double demNorth = raster.North;

        var result = new List<MarkerWorldPoint<ClimbingArea>>(areas.Count);
        foreach (var area in areas)
        {
            double lon = area.Position.Longitude;
            double lat = area.Position.Latitude;
            if (lon < demWest || lon > demEast || lat < demSouth || lat > demNorth)
            {
                continue;
            }

            float groundElevation = (float)raster.SampleBilinear(lon, lat);
            float liftedElevation = groundElevation + markerLiftMeters;
            Vector3 world = mesh.GeoToWorld(area.Position, liftedElevation);
            result.Add(new MarkerWorldPoint<ClimbingArea>(area, world));
        }

        return result;
    }
}