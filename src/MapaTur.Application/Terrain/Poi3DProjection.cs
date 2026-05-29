using System.Numerics;

using MapaTur.Domain.Pois;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Camera-independent world-space build for <see cref="MountainPoi"/> markers: lifts each POI inside the
/// DEM to its sampled ground elevation and converts it to mesh world space. Feeds the generic
/// <see cref="Marker3DOverlayProjector{TSource, TProjected}"/> (like climbing areas).
/// </summary>
public static class Poi3DProjection
{
    /// <summary>
    /// Projects every POI in <paramref name="pois"/> whose lon/lat falls inside the raster's bounding box
    /// onto the 3D viewport. POIs entirely outside the DEM are dropped — they have no defined elevation
    /// and would render at the wrong position anyway. Mirrors <see cref="Climbing3DProjection.Project"/>.
    /// </summary>
    /// <param name="pois">POIs to project.</param>
    /// <param name="raster">Source DEM used to look up ground elevation.</param>
    /// <param name="mesh">Mesh whose world-space convention defines the coordinate system.</param>
    /// <param name="camera">Camera providing view + projection matrices.</param>
    /// <param name="screenWidth">Viewport width in pixels.</param>
    /// <param name="screenHeight">Viewport height in pixels.</param>
    /// <param name="markerLiftMeters">Vertical offset above the DEM surface so the marker sits clear of the ground.</param>
    public static IReadOnlyList<ProjectedPoi> Project(
        IReadOnlyList<MountainPoi> pois,
        DemRaster raster,
        TerrainMesh3D mesh,
        Camera3D camera,
        float screenWidth,
        float screenHeight,
        float markerLiftMeters = 25f)
    {
        ArgumentNullException.ThrowIfNull(pois);
        ArgumentNullException.ThrowIfNull(raster);
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(camera);

        if (pois.Count == 0)
        {
            return Array.Empty<ProjectedPoi>();
        }

        Matrix4x4 viewProjection = (screenWidth > 0f && screenHeight > 0f)
            ? camera.BuildViewProjection(screenWidth / screenHeight)
            : Matrix4x4.Identity;

        IReadOnlyList<MarkerWorldPoint<MountainPoi>> world = ToWorld(pois, raster, mesh, markerLiftMeters);
        var result = new List<ProjectedPoi>(world.Count);
        foreach (MarkerWorldPoint<MountainPoi> marker in world)
        {
            Vector3? screen = camera.ProjectToScreen(marker.World, viewProjection, screenWidth, screenHeight);
            result.Add(new ProjectedPoi(marker.Source, screen));
        }

        return result;
    }

    /// <summary>
    /// Lifts every POI whose lon/lat falls inside the raster to its DEM elevation + <paramref name="markerLiftMeters"/>
    /// and converts it to world space. POIs outside the DEM are dropped.
    /// </summary>
    public static IReadOnlyList<MarkerWorldPoint<MountainPoi>> ToWorld(
        IReadOnlyList<MountainPoi> pois,
        DemRaster raster,
        TerrainMesh3D mesh,
        float markerLiftMeters = 25f)
    {
        ArgumentNullException.ThrowIfNull(pois);
        ArgumentNullException.ThrowIfNull(raster);
        ArgumentNullException.ThrowIfNull(mesh);

        double demWest = raster.West;
        double demEast = raster.East;
        double demSouth = raster.South;
        double demNorth = raster.North;

        var result = new List<MarkerWorldPoint<MountainPoi>>(pois.Count);
        foreach (MountainPoi poi in pois)
        {
            double lon = poi.Position.Longitude;
            double lat = poi.Position.Latitude;
            if (lon < demWest || lon > demEast || lat < demSouth || lat > demNorth)
            {
                continue;
            }

            float groundElevation = (float)raster.SampleBilinear(lon, lat);
            Vector3 world = mesh.GeoToWorld(poi.Position, groundElevation + markerLiftMeters);
            result.Add(new MarkerWorldPoint<MountainPoi>(poi, world));
        }

        return result;
    }
}