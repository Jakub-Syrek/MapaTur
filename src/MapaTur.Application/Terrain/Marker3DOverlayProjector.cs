using System.Numerics;

using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Stateful, per-view projector for single-point map markers (climbing areas, summits, …). It owns the
/// camera-independent world cache plus a reusable results buffer, so a host that re-renders every frame
/// during a gesture allocates nothing per frame — only the view+projection transform runs, writing each
/// projected marker into the cached array in place.
/// <para>
/// The world cache is rebuilt (via the injected <c>worldBuilder</c>) only when the marker list, raster
/// or mesh reference, or the lift value changes. Markers the builder drops (e.g. a climbing area outside
/// the loaded DEM) are simply absent from the cache, so the projected output has exactly one entry per
/// surviving marker. This generic replaces a separate hand-written projector per marker type — climbing
/// and summit overlays differ only in their tiny <c>worldBuilder</c>/result-factory, not in this caching
/// and per-frame machinery.
/// </para>
/// Hold one instance per marker layer per view. Not thread-safe; call <see cref="Project"/> from the
/// render thread only.
/// </summary>
/// <typeparam name="TSource">Originating marker type.</typeparam>
/// <typeparam name="TProjected">Projected marker type returned to the renderer.</typeparam>
public sealed class Marker3DOverlayProjector<TSource, TProjected>
{
    private readonly Func<IReadOnlyList<TSource>, DemRaster?, TerrainMesh3D, float, IReadOnlyList<MarkerWorldPoint<TSource>>> worldBuilder;
    private readonly Func<TSource, Vector3?, TProjected> resultFactory;

    private IReadOnlyList<MarkerWorldPoint<TSource>>? worldCache;
    private IReadOnlyList<TSource>? cachedItems;
    private DemRaster? cachedRaster;
    private TerrainMesh3D? cachedMesh;
    private float cachedLift;

    private TProjected[]? results;

    /// <summary>
    /// Initializes the projector with the per-type world-build and result-construction strategies.
    /// </summary>
    /// <param name="worldBuilder">
    /// Camera-independent build of world-space markers from the inputs (lift markers to their elevation,
    /// convert geo→world, drop out-of-scope markers). Invoked only on a cache miss, never per frame.
    /// </param>
    /// <param name="resultFactory">Wraps a source marker + its (possibly null) screen position into the projected type.</param>
    public Marker3DOverlayProjector(
        Func<IReadOnlyList<TSource>, DemRaster?, TerrainMesh3D, float, IReadOnlyList<MarkerWorldPoint<TSource>>> worldBuilder,
        Func<TSource, Vector3?, TProjected> resultFactory)
    {
        ArgumentNullException.ThrowIfNull(worldBuilder);
        ArgumentNullException.ThrowIfNull(resultFactory);
        this.worldBuilder = worldBuilder;
        this.resultFactory = resultFactory;
    }

    /// <summary>
    /// Projects <paramref name="items"/> to screen space, reusing the cached world points and results
    /// buffer whenever the inputs are unchanged since the last call.
    /// </summary>
    /// <param name="items">Markers to project.</param>
    /// <param name="raster">Source DEM, when the world build needs ground elevations (null for self-elevating markers like summits).</param>
    /// <param name="mesh">Mesh whose world-space convention defines the coordinate system.</param>
    /// <param name="camera">Camera providing view + projection matrices.</param>
    /// <param name="screenWidth">Viewport width in pixels.</param>
    /// <param name="screenHeight">Viewport height in pixels.</param>
    /// <param name="liftMeters">Vertical offset above the surface so the marker sits clear of the ground.</param>
    public IReadOnlyList<TProjected> Project(
        IReadOnlyList<TSource> items,
        DemRaster? raster,
        TerrainMesh3D mesh,
        Camera3D camera,
        float screenWidth,
        float screenHeight,
        float liftMeters)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(camera);

        if (worldCache is null
            || !ReferenceEquals(cachedItems, items)
            || !ReferenceEquals(cachedRaster, raster)
            || !ReferenceEquals(cachedMesh, mesh)
            || cachedLift != liftMeters)
        {
            worldCache = worldBuilder(items, raster, mesh, liftMeters);
            cachedItems = items;
            cachedRaster = raster;
            cachedMesh = mesh;
            cachedLift = liftMeters;
            results = new TProjected[worldCache.Count];
        }

        Matrix4x4 viewProjection = (screenWidth > 0f && screenHeight > 0f)
            ? camera.BuildViewProjection(screenWidth / screenHeight)
            : Matrix4x4.Identity;

        var buffer = results!;
        for (int i = 0; i < worldCache.Count; i++)
        {
            MarkerWorldPoint<TSource> marker = worldCache[i];
            Vector3? screen = camera.ProjectToScreen(marker.World, viewProjection, screenWidth, screenHeight);
            buffer[i] = resultFactory(marker.Source, screen);
        }

        return buffer;
    }
}