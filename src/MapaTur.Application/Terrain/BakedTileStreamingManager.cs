using System.Numerics;

using MapaTur.Domain.Geography;

namespace MapaTur.Application.Terrain;

/// <summary>The outcome of one <see cref="BakedTileStreamingManager.UpdateAsync"/>: the resident drawable set
/// plus what changed, for the caller to hand to the renderer and log.</summary>
/// <param name="ResidentTiles">The currently-resident baked tiles to draw, nearest-camera first.</param>
/// <param name="Loaded">Tiles loaded + meshed this update (capped to the max-concurrent-loads budget).</param>
/// <param name="Evicted">Tiles dropped this update (no longer desired).</param>
/// <param name="Desired">Size of the selector's desired set this update (the streaming target).</param>
/// <param name="WasClampedByBudget">True when the residency cap coarsened/clamped the selection.</param>
public sealed record BakedStreamingUpdate(
    IReadOnlyList<TerrainMesh3D> ResidentTiles,
    int Loaded,
    int Evicted,
    int Desired,
    bool WasClampedByBudget);

/// <summary>
/// Stage 2c of the terrain re-architecture: the streaming driver that turns a moving camera into a resident
/// set of pre-baked, pre-repaired DEM tiles drawn by the existing terrain shader — with NO per-move mosaic,
/// repair or window rebuild (the whole point of the bake).
///
/// On each <see cref="UpdateAsync"/> it runs the pure <see cref="QuadtreeTileSelector"/> (finest near the eye,
/// coarsening with distance, frustum-culled, residency-capped) to get the desired tiles, diffs that against the
/// resident set with <see cref="TileResidencyPlanner"/> (load near→far capped per call, evict farthest-first),
/// then for each to-load key reads its <see cref="BakedDemTile"/> through the injected loader and meshes it with
/// <see cref="BakedTileMeshBuilder"/> (same world frame / vertex layout / ortho-UV as the live terrain) on a
/// background thread. The finished meshes join the resident set; evicted keys are dropped. The resident
/// <see cref="TerrainMesh3D"/> set is returned for the renderer's existing delta-upload tile path.
///
/// The disk read is isolated behind the loader delegate (the real one reads <c>.bdt</c> files; tests supply a
/// fake), so the selection / residency policy is deterministic and unit-testable without disk or GL. Not
/// thread-safe: drive it from one place (the camera-focus handler), one update at a time.
/// </summary>
public sealed class BakedTileStreamingManager
{
    private readonly IReadOnlyList<DemTileKey> roots;
    private readonly Func<DemTileKey, bool> isBaked;
    private readonly Func<DemTileKey, BakedDemTile?> loadTile;
    private readonly GeoPoint projectionAnchor;
    private readonly int minZoom;
    private readonly int maxZoom;
    private readonly int maxResidentTiles;
    private readonly long maxResidentBytes;
    private readonly int maxConcurrentLoads;
    private readonly double maxErrorPixels;
    private readonly Func<DemTileKey, float> skirtDepthMeters;
    private readonly TerrainMeshOptions? meshOptions;
    private readonly OrthoCoverage? orthoCoverage;
    private readonly int orthoTileIndexOffset;

    // Resident tiles keyed by slippy address, plus the near→far order from the last selection (so eviction
    // releases the far field first — TileResidencyPlanner takes the resident order and reverses it). Each key maps
    // to ONE OR MORE meshes: a tile straddling an ortho cell boundary is cut into per-cell sub-meshes (the
    // anti-stripe fix), all sharing the tile's residency lifecycle.
    private readonly Dictionary<DemTileKey, IReadOnlyList<TerrainMesh3D>> resident = new();
    private List<DemTileKey> residentOrder = new();

    // Per resident key: true when the baked tile had NO NoData (so its mesh dropped no triangles → no see-through
    // holes). Only such tiles may OCCLUDE the coarse base (a holey tile relies on the base showing through its
    // gaps). Kept in lock-step with <see cref="resident"/> (set on load, removed on evict / Clear).
    private readonly Dictionary<DemTileKey, bool> holeFreeByKey = new();

    // Default geometry-byte cap when a caller doesn't specify one: large enough that the count cap stays the
    // binding limit for normal scenes, so behaviour is unchanged unless geometry actually balloons.
    private const long DefaultMaxResidentBytes = long.MaxValue;

    /// <summary>
    /// Creates a streaming manager.
    /// </summary>
    /// <param name="roots">Quadtree roots covering the baked region (usually the baked pyramid's coarsest level).</param>
    /// <param name="isBaked">Availability predicate (a tile refines into its children only when all four are baked).</param>
    /// <param name="loadTile">Loads one baked tile by key (real: read its <c>.bdt</c>; returns null when absent/corrupt).</param>
    /// <param name="projectionAnchor">Shared LOD world-frame origin every tile is meshed about (must match the scene).</param>
    /// <param name="minZoom">Coarsest zoom to consider (the roots' zoom).</param>
    /// <param name="maxZoom">Finest zoom to refine to.</param>
    /// <param name="maxResidentTiles">Residency cap by tile COUNT (the far field is coarsened to fit).</param>
    /// <param name="maxConcurrentLoads">Most tiles loaded+meshed per update (a big jump streams over several updates).</param>
    /// <param name="maxErrorPixels">Screen-space pixel error budget driving refinement.</param>
    /// <param name="skirtDepthMeters">Per-tile downward skirt depth (fills LOD-seam cracks); keyed on the tile so a
    /// coarser tile can hang a deeper skirt. Returns 0 to disable skirts.</param>
    /// <param name="meshOptions">Mesh tuning (exaggeration, light, ambient) — must match the live terrain so baked
    /// tiles shade identically. Null uses <see cref="TerrainMeshOptions"/> defaults.</param>
    /// <param name="orthoCoverage">Optional ortho placement so baked tiles are textured through the existing ortho
    /// path (geo-referenced UV + per-tile <see cref="TerrainMesh3D.OrthoTileIndex"/>). Null = hypsometric.</param>
    /// <param name="orthoTileIndexOffset">Added to each tile's ortho cell index so baked cells line up with the
    /// renderer's ortho list (0 when baked tiles share the base scene's coverage grid).</param>
    /// <param name="maxResidentBytes">Residency cap by total resident tile GEOMETRY bytes
    /// (<see cref="TerrainMesh3D.EstimatedGpuBytes"/>). Capped by BOTH this and <paramref name="maxResidentTiles"/>:
    /// eviction (farthest-first) runs while resident is over EITHER limit. Defaults to effectively unbounded so the
    /// count cap stays binding unless a caller opts in. ≥ 1.</param>
    /// <exception cref="ArgumentNullException">A required reference argument is null.</exception>
    /// <exception cref="ArgumentException">The zoom range is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A budget is below its minimum.</exception>
    public BakedTileStreamingManager(
        IReadOnlyList<DemTileKey> roots,
        Func<DemTileKey, bool> isBaked,
        Func<DemTileKey, BakedDemTile?> loadTile,
        GeoPoint projectionAnchor,
        int minZoom,
        int maxZoom,
        int maxResidentTiles,
        int maxConcurrentLoads,
        double maxErrorPixels,
        Func<DemTileKey, float> skirtDepthMeters,
        TerrainMeshOptions? meshOptions = null,
        OrthoCoverage? orthoCoverage = null,
        int orthoTileIndexOffset = 0,
        long maxResidentBytes = DefaultMaxResidentBytes)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(isBaked);
        ArgumentNullException.ThrowIfNull(loadTile);
        ArgumentNullException.ThrowIfNull(skirtDepthMeters);
        if (maxZoom < minZoom)
        {
            throw new ArgumentException("maxZoom must be at least minZoom.", nameof(maxZoom));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(maxResidentTiles, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrentLoads, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxResidentBytes, 1L);

        this.roots = roots;
        this.isBaked = isBaked;
        this.loadTile = loadTile;
        this.projectionAnchor = projectionAnchor;
        this.minZoom = minZoom;
        this.maxZoom = maxZoom;
        this.maxResidentTiles = maxResidentTiles;
        this.maxResidentBytes = maxResidentBytes;
        this.maxConcurrentLoads = maxConcurrentLoads;
        this.maxErrorPixels = maxErrorPixels;
        this.skirtDepthMeters = skirtDepthMeters;
        this.meshOptions = meshOptions;
        this.orthoCoverage = orthoCoverage;
        this.orthoTileIndexOffset = orthoTileIndexOffset;
    }

    /// <summary>Tiles currently resident (loaded + meshed).</summary>
    public int ResidentCount => this.resident.Count;

    /// <summary>
    /// Resident keys that are HOLE-FREE — safe to use as base-tile occluders (a coarse base tile fully under
    /// these can be skipped without exposing a gap). A baked tile with NoData (dropped triangles) is excluded
    /// because the base must still backfill its holes. In current near→far resident order.
    /// </summary>
    public IReadOnlyList<DemTileKey> OccludingKeys
    {
        get
        {
            var keys = new List<DemTileKey>(this.resident.Count);
            foreach (DemTileKey key in this.residentOrder)
            {
                if (this.holeFreeByKey.TryGetValue(key, out bool holeFree) && holeFree)
                {
                    keys.Add(key);
                }
            }

            return keys;
        }
    }

    /// <summary>Estimated geometry bytes of every resident tile (sum of <see cref="TerrainMesh3D.EstimatedGpuBytes"/>);
    /// what the byte budget caps and what the memory log reports.</summary>
    public long ResidentBytes
    {
        get
        {
            long total = 0;
            foreach (IReadOnlyList<TerrainMesh3D> meshes in this.resident.Values)
            {
                total += KeyBytes(meshes);
            }

            return total;
        }
    }

    /// <summary>Sum of one key's sub-mesh geometry bytes.</summary>
    private static long KeyBytes(IReadOnlyList<TerrainMesh3D> meshes)
    {
        long total = 0;
        foreach (TerrainMesh3D mesh in meshes)
        {
            total += mesh.EstimatedGpuBytes;
        }

        return total;
    }

    /// <summary>
    /// Advances streaming for one camera pose: selects the desired tiles, loads up to the per-call budget on a
    /// background thread, evicts what's no longer wanted, and returns the resident set + what changed.
    /// </summary>
    /// <param name="camera">The current camera (drives selection + distance ordering).</param>
    /// <param name="aspectRatio">Viewport aspect ratio (width / height) for the view-projection.</param>
    /// <param name="viewportHeightPixels">Viewport height in pixels for the screen-space-error projection.</param>
    /// <returns>The resident drawable set and the load/evict deltas.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="camera"/> is null.</exception>
    public async Task<BakedStreamingUpdate> UpdateAsync(Camera3D camera, float aspectRatio, double viewportHeightPixels)
    {
        ArgumentNullException.ThrowIfNull(camera);

        float groundElevation = camera.Target.Z; // the look-at height is a good ground proxy for distance/frustum
        var options = new QuadtreeTileSelectorOptions
        {
            Camera = camera,
            Roots = this.roots,
            ProjectionAnchor = this.projectionAnchor,
            GroundElevationMeters = groundElevation,
            VerticalExaggeration = this.meshOptions?.VerticalExaggeration ?? 1f,
            MinZoom = this.minZoom,
            MaxZoom = this.maxZoom,
            AspectRatio = aspectRatio,
            ViewportHeightPixels = Math.Max(1.0, viewportHeightPixels),
            MaxErrorPixels = this.maxErrorPixels,
            MaxResidentTiles = this.maxResidentTiles,
            IsBaked = this.isBaked,
        };

        QuadtreeTileSelection selection = QuadtreeTileSelector.Select(options);
        var desired = new List<DemTileKey>(selection.Tiles.Count);
        foreach (SelectedTile t in selection.Tiles)
        {
            desired.Add(t.Key);
        }

        TileResidencyDiff diff = TileResidencyPlanner.Plan(this.residentOrder, desired, this.maxConcurrentLoads);
        var previousOrder = this.residentOrder;

        // Load+mesh the to-load set OFF the calling thread so a camera move never stalls on disk read + meshing.
        // We deliberately do NOT evict a tile just because it left the desired set: keeping it resident (still
        // drawn) UNDER the cap means a small camera move doesn't lose the detail it just had (the "detail vanishes
        // on move" flicker). Resident is typically far below the cap, so there's room to cache. Eviction is
        // CAP-DRIVEN below — only when resident exceeds maxResidentTiles, farthest/oldest first.
        int loaded = 0;
        if (diff.ToLoad.Count > 0)
        {
            IReadOnlyList<(DemTileKey Key, IReadOnlyList<TerrainMesh3D> Meshes, bool HoleFree)> built =
                await Task.Run(() => BuildTiles(diff.ToLoad)).ConfigureAwait(false);
            foreach ((DemTileKey key, IReadOnlyList<TerrainMesh3D> meshes, bool holeFree) in built)
            {
                this.resident[key] = meshes;
                this.holeFreeByKey[key] = holeFree;
                loaded++;
            }
        }

        // Draw/keep order: the desired tiles near→far first, then the retained (now off-desired) tiles in their
        // prior order at the far end — so the cap trim and the next planner both shed the farthest/oldest first.
        var orderedResident = new List<DemTileKey>(this.resident.Count);
        var seen = new HashSet<DemTileKey>();
        foreach (DemTileKey key in desired)
        {
            if (this.resident.ContainsKey(key) && seen.Add(key))
            {
                orderedResident.Add(key);
            }
        }

        foreach (DemTileKey key in previousOrder)
        {
            if (this.resident.ContainsKey(key) && seen.Add(key))
            {
                orderedResident.Add(key);
            }
        }

        // Cap-driven eviction: drop the farthest/oldest (tail) while over EITHER the tile-count cap OR the
        // geometry-byte cap. A broad ring of baked 1 m tiles can blow the byte budget long before the count
        // cap, which is exactly the OOM at the route film's lowest point — so bound resident bytes too. The
        // RING SELECTION upstream stays broad; this only bounds what is kept resident.
        long residentBytes = 0;
        foreach (DemTileKey key in orderedResident)
        {
            residentBytes += KeyBytes(this.resident[key]);
        }

        int evicted = 0;
        // Over the COUNT cap can evict down to the cap; over the BYTE cap keeps at least one tile resident (you
        // can't render a hole, and a single tile bigger than the whole byte budget is pathological, not a leak).
        while ((orderedResident.Count > this.maxResidentTiles) ||
            (orderedResident.Count > 1 && residentBytes > this.maxResidentBytes))
        {
            DemTileKey far = orderedResident[orderedResident.Count - 1];
            orderedResident.RemoveAt(orderedResident.Count - 1);
            residentBytes -= KeyBytes(this.resident[far]);
            this.resident.Remove(far);
            this.holeFreeByKey.Remove(far);
            evicted++;
        }

        var drawable = new List<TerrainMesh3D>(orderedResident.Count);
        foreach (DemTileKey key in orderedResident)
        {
            drawable.AddRange(this.resident[key]);
        }

        this.residentOrder = orderedResident;
        return new BakedStreamingUpdate(drawable, loaded, evicted, desired.Count, selection.WasClampedByBudget);
    }

    /// <summary>Drops every resident tile (e.g. a new scene loads, or the anchor / ortho coverage changed).</summary>
    public void Clear()
    {
        this.resident.Clear();
        this.holeFreeByKey.Clear();
        this.residentOrder = new List<DemTileKey>();
    }

    // Loads + meshes each to-load key. A key whose loader returns null (missing/corrupt tile) is skipped — it
    // simply stays un-resident and reappears in the next selection's to-load until it can be read. A tile that
    // straddles an ortho cell boundary is cut into per-cell sub-meshes (BuildCut), so the result is one OR MORE
    // meshes per key — the anti-stripe fix (§B.3).
    private List<(DemTileKey Key, IReadOnlyList<TerrainMesh3D> Meshes, bool HoleFree)> BuildTiles(IReadOnlyList<DemTileKey> toLoad)
    {
        var built = new List<(DemTileKey, IReadOnlyList<TerrainMesh3D>, bool)>(toLoad.Count);
        foreach (DemTileKey key in toLoad)
        {
            BakedDemTile? tile = this.loadTile(key);
            if (tile is null)
            {
                continue;
            }

            IReadOnlyList<TerrainMesh3D> meshes = BakedTileMeshBuilder.BuildCut(
                tile, this.projectionAnchor, this.meshOptions, this.skirtDepthMeters(key),
                this.orthoCoverage, this.orthoTileIndexOffset);
            built.Add((key, meshes, IsHoleFree(tile)));
        }

        return built;
    }

    // A baked tile is hole-free when no sample is NoData — then its mesh dropped no triangles, so nothing
    // see-through remains and the coarse base underneath it is pure overdraw (safe to skip). The bake's
    // FillNoDataFrom(base) backfills voids, so core tiles are hole-free; only tiles with residual NoData (base
    // also missing at a far edge) keep the base showing through.
    private static bool IsHoleFree(BakedDemTile tile)
    {
        var noData = (float)tile.NoDataValue;
        float[] heights = tile.Heights;
        for (int i = 0; i < heights.Length; i++)
        {
            if (heights[i] == noData)
            {
                return false;
            }
        }

        return true;
    }
}