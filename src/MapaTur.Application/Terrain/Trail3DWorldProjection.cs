using System.Numerics;

using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;
using MapaTur.Domain.Trails;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Two-stage trail projection that splits the camera-independent work from the per-frame work.
/// <para>
/// <see cref="ToWorld"/> does the expensive, camera-independent part — DEM bbox culling, bilinear
/// elevation sampling and geo→world conversion — which depends only on the trails, raster and mesh.
/// <see cref="ToScreen"/> does the cheap per-frame part — the camera view+projection transform.
/// </para>
/// During a gesture the camera changes every frame but the trails/raster/mesh do not, so the host
/// caches the <see cref="ToWorld"/> result once and only re-runs <see cref="ToScreen"/> per frame,
/// eliminating tens of thousands of bilinear samples + cosines per frame that used to crush gesture
/// smoothness. <see cref="Trail3DProjection.Project"/> is the eager wrapper that runs both stages.
/// </summary>
public static class Trail3DWorldProjection
{
    /// <summary>
    /// Lifts every trail vertex to its DEM elevation and converts it into mesh world space. Trails
    /// whose lon/lat bbox doesn't intersect the DEM are dropped (most downloaded trails lie outside
    /// the small loaded DEM patch). Camera-independent — compute once and reuse across frames.
    /// </summary>
    /// <param name="trails">Trails to convert.</param>
    /// <param name="raster">Source DEM used to look up elevations along each trail.</param>
    /// <param name="mesh">Mesh whose world-space convention defines the coordinate system.</param>
    /// <param name="trailLiftMeters">Vertical offset added to each vertex (before exaggeration) so trails sit above the mesh surface.</param>
    public static IReadOnlyList<TrailWorldLine> ToWorld(
        IReadOnlyList<Trail> trails,
        DemRaster raster,
        TerrainMesh3D mesh,
        float trailLiftMeters = 5f)
    {
        ArgumentNullException.ThrowIfNull(trails);
        ArgumentNullException.ThrowIfNull(raster);
        ArgumentNullException.ThrowIfNull(mesh);

        double demWest = raster.West;
        double demEast = raster.East;
        double demSouth = raster.South;
        double demNorth = raster.North;

        var result = new List<TrailWorldLine>(trails.Count);
        foreach (Trail trail in trails)
        {
            if (!TrailBboxIntersectsDem(trail.Geometry, demWest, demEast, demSouth, demNorth))
            {
                continue;
            }

            var world = new Vector3[trail.Geometry.Count];
            for (int i = 0; i < trail.Geometry.Count; i++)
            {
                var geo = trail.Geometry[i];
                float groundElevation = (float)raster.SampleBilinear(geo.Longitude, geo.Latitude);
                world[i] = mesh.GeoToWorld(geo, groundElevation + trailLiftMeters);
            }

            result.Add(new TrailWorldLine(trail, world));
        }

        return result;
    }

    /// <summary>
    /// Projects pre-computed <see cref="TrailWorldLine"/>s to screen space through the camera. This
    /// is the only per-frame stage; it builds the view+projection once and runs each world point
    /// through it. A vertex behind the camera or outside the clip range projects to null.
    /// </summary>
    /// <param name="worldLines">World-space trails from <see cref="ToWorld"/>.</param>
    /// <param name="camera">Camera providing view + projection matrices.</param>
    /// <param name="screenWidth">Viewport width in pixels.</param>
    /// <param name="screenHeight">Viewport height in pixels.</param>
    public static IReadOnlyList<ProjectedTrail> ToScreen(
        IReadOnlyList<TrailWorldLine> worldLines,
        Camera3D camera,
        float screenWidth,
        float screenHeight)
    {
        ArgumentNullException.ThrowIfNull(worldLines);
        ArgumentNullException.ThrowIfNull(camera);

        Matrix4x4 viewProjection = (screenWidth > 0f && screenHeight > 0f)
            ? camera.BuildViewProjection(screenWidth / screenHeight)
            : Matrix4x4.Identity;

        var result = new List<ProjectedTrail>(worldLines.Count);
        foreach (TrailWorldLine line in worldLines)
        {
            var points = new Vector3?[line.World.Count];
            for (int i = 0; i < line.World.Count; i++)
            {
                points[i] = camera.ProjectToScreen(line.World[i], viewProjection, screenWidth, screenHeight);
            }

            result.Add(new ProjectedTrail(line.Source, points));
        }

        return result;
    }

    private static bool TrailBboxIntersectsDem(
        IReadOnlyList<GeoPoint> geometry,
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