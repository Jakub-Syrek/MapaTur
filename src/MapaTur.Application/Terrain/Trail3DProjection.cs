using System.Numerics;
using MapaTur.Domain.Terrain;
using MapaTur.Domain.Trails;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Projects <see cref="Trail"/> polylines onto the 3D viewport. Each trail vertex
/// is lifted to the underlying DEM elevation (plus a small bias so the line sits
/// above the terrain) and run through the camera's view+projection pipeline.
/// </summary>
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
        ArgumentNullException.ThrowIfNull(trails);
        ArgumentNullException.ThrowIfNull(raster);
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(camera);

        // Build view * projection once per call — projecting every trail vertex would
        // otherwise rebuild view + projection matrices N times per frame.
        Matrix4x4 viewProjection = (screenWidth > 0f && screenHeight > 0f)
            ? camera.BuildViewProjection(screenWidth / screenHeight)
            : Matrix4x4.Identity;

        double demWest = raster.West;
        double demEast = raster.East;
        double demSouth = raster.South;
        double demNorth = raster.North;

        var result = new List<ProjectedTrail>(trails.Count);
        foreach (Trail trail in trails)
        {
            // Cull trails whose lon/lat bbox doesn't intersect the DEM bbox.
            // At viewport-trail counts of 700+ this removes ~90% of the per-frame
            // projection work, since most downloaded trails lie outside the
            // small DEM patch the user has loaded.
            if (!TrailBboxIntersectsDem(trail.Geometry, demWest, demEast, demSouth, demNorth))
            {
                continue;
            }

            var points = new Vector3?[trail.Geometry.Count];
            for (int i = 0; i < trail.Geometry.Count; i++)
            {
                var geo = trail.Geometry[i];
                float groundElevation = (float)raster.SampleBilinear(geo.Longitude, geo.Latitude);
                float liftedElevation = groundElevation + trailLiftMeters;
                Vector3 world = mesh.GeoToWorld(geo, liftedElevation);
                points[i] = camera.ProjectToScreen(world, viewProjection, screenWidth, screenHeight);
            }

            result.Add(new ProjectedTrail(trail, points));
        }

        return result;
    }

    private static bool TrailBboxIntersectsDem(
        IReadOnlyList<MapaTur.Domain.Geography.GeoPoint> geometry,
        double demWest,
        double demEast,
        double demSouth,
        double demNorth)
    {
        if (geometry.Count == 0)
        {
            return false;
        }

        double minLon = double.MaxValue, maxLon = double.MinValue;
        double minLat = double.MaxValue, maxLat = double.MinValue;
        for (int i = 0; i < geometry.Count; i++)
        {
            var p = geometry[i];
            if (p.Longitude < minLon) minLon = p.Longitude;
            if (p.Longitude > maxLon) maxLon = p.Longitude;
            if (p.Latitude < minLat) minLat = p.Latitude;
            if (p.Latitude > maxLat) maxLat = p.Latitude;
        }

        return maxLon >= demWest && minLon <= demEast
            && maxLat >= demSouth && minLat <= demNorth;
    }
}
