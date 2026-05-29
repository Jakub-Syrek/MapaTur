using System.Numerics;

using MapaTur.Domain.Terrain;
using MapaTur.Domain.Trails;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Stateful, per-view trail projector that owns the camera-independent world cache from
/// <see cref="Trail3DWorldProjection.ToWorld"/> and a set of reusable screen buffers, so a host that
/// re-renders every frame during a gesture allocates nothing per frame.
/// <para>
/// The world cache (DEM bbox culling, bilinear elevation sampling, geo→world conversion) is rebuilt
/// only when the trails, raster, mesh or lift change by reference/value. Every frame
/// <see cref="Project"/> rebuilds the view+projection once and fills the cached buffers in place, then
/// returns the same backing arrays — values change, references don't.
/// </para>
/// Hold one instance per <c>Terrain3DView</c>. It is not thread-safe; call <see cref="Project"/> from
/// the render thread only.
/// </summary>
public sealed class Trail3DOverlayProjector
{
    private IReadOnlyList<TrailWorldLine>? worldCache;
    private IReadOnlyList<Trail>? cachedTrails;
    private DemRaster? cachedRaster;
    private TerrainMesh3D? cachedMesh;
    private float cachedLift;

    private Vector3?[][]? screenBuffers;
    private ProjectedTrail[]? results;

    /// <summary>
    /// Projects <paramref name="trails"/> to screen space, reusing the cached world points and screen
    /// buffers whenever the inputs are unchanged since the last call.
    /// </summary>
    /// <param name="trails">Trails to project.</param>
    /// <param name="raster">Source DEM used to look up elevations along each trail.</param>
    /// <param name="mesh">Mesh whose world-space convention defines the coordinate system.</param>
    /// <param name="camera">Camera providing view + projection matrices.</param>
    /// <param name="screenWidth">Viewport width in pixels.</param>
    /// <param name="screenHeight">Viewport height in pixels.</param>
    /// <param name="trailLiftMeters">Vertical offset added to each vertex before exaggeration so trails sit above the mesh surface.</param>
    public IReadOnlyList<ProjectedTrail> Project(
        IReadOnlyList<Trail> trails,
        DemRaster raster,
        TerrainMesh3D mesh,
        Camera3D camera,
        float screenWidth,
        float screenHeight,
        float trailLiftMeters = 5f)
    {
        ArgumentNullException.ThrowIfNull(camera);

        if (worldCache is null
            || !ReferenceEquals(cachedTrails, trails)
            || !ReferenceEquals(cachedRaster, raster)
            || !ReferenceEquals(cachedMesh, mesh)
            || cachedLift != trailLiftMeters)
        {
            // ToWorld validates trails/raster/mesh.
            worldCache = Trail3DWorldProjection.ToWorld(trails, raster, mesh, trailLiftMeters);
            cachedTrails = trails;
            cachedRaster = raster;
            cachedMesh = mesh;
            cachedLift = trailLiftMeters;
            AllocateBuffers(worldCache);
        }

        Matrix4x4 viewProjection = (screenWidth > 0f && screenHeight > 0f)
            ? camera.BuildViewProjection(screenWidth / screenHeight)
            : Matrix4x4.Identity;

        for (int li = 0; li < worldCache.Count; li++)
        {
            var world = worldCache[li].World;
            var buffer = screenBuffers![li];
            for (int i = 0; i < world.Count; i++)
            {
                buffer[i] = camera.ProjectToScreen(world[i], viewProjection, screenWidth, screenHeight);
            }
        }

        return results!;
    }

    private void AllocateBuffers(IReadOnlyList<TrailWorldLine> lines)
    {
        var buffers = new Vector3?[lines.Count][];
        var projected = new ProjectedTrail[lines.Count];
        for (int li = 0; li < lines.Count; li++)
        {
            var buffer = new Vector3?[lines[li].World.Count];
            buffers[li] = buffer;
            projected[li] = new ProjectedTrail(lines[li].Source, buffer);
        }

        screenBuffers = buffers;
        results = projected;
    }
}