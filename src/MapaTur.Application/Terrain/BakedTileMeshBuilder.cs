using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Stage 2b of the terrain re-architecture: turns ONE <see cref="BakedDemTile"/> into a drawable
/// <see cref="TerrainMesh3D"/> at FULL resolution, in the SAME world frame, vertex layout and
/// ortho-UV / hypsometric-colour conventions as the live terrain — so the existing terrain shader
/// draws a baked tile unchanged and it lines up with everything else on the ground.
///
/// It reuses <see cref="TerrainMesh3D"/>'s own meshing rather than reinventing the vertex/index/normal
/// layout: the baked tile's row-major height grid is wrapped as a <see cref="DemRaster"/> over the same
/// geographic <see cref="BakedDemTile.Bounds"/> (identical row/column convention and NoData sentinel), and
/// <see cref="TerrainMesh3D.Build(DemRaster, TerrainMeshOptions)"/> meshes that single-tile raster as one
/// block at full resolution (no subsample). Because the world frame is anchored on a shared
/// <see cref="TerrainMesh3D.ProjectionAnchor"/> (the LOD scene origin), every baked tile projects into one consistent
/// coordinate system and a vertex lands exactly where <see cref="LocalTangentProjection.GeoToWorld"/> would
/// put the same ground point about that shared anchor.
///
/// Pure: no GL, no disk I/O, no live-render-path involvement. Output is a deterministic function of the
/// baked tile and the build parameters.
/// </summary>
public static class BakedTileMeshBuilder
{
    /// <summary>
    /// Builds a full-resolution <see cref="TerrainMesh3D"/> from <paramref name="tile"/>.
    /// </summary>
    /// <param name="tile">The baked DEM tile to mesh (its repaired height grid + geographic bounds).</param>
    /// <param name="projectionAnchor">Shared world-frame origin (the LOD scene anchor). All baked tiles meshed
    /// against the same anchor share one coordinate system, so they line up with each other and with the live
    /// terrain. The tile's own world position is the offset of its bounds centre from this anchor.</param>
    /// <param name="options">Mesh tuning (vertical exaggeration, light, ambient, normal smoothing). Null uses
    /// <see cref="TerrainMeshOptions"/> defaults. Any <see cref="TerrainMeshOptions.SkirtDepthMeters"/> on it is
    /// ignored in favour of the explicit <paramref name="skirtDepthMeters"/> argument below.</param>
    /// <param name="skirtDepthMeters">Downward skirt depth in metres hung from the tile's border so a finer
    /// neighbour tile at a different LOD doesn't show a crack at the seam. 0 (default) builds no skirt. The
    /// skirt only fills the vertical gap at the border: the visible top-surface vertices are byte-identical to a
    /// no-skirt build (they are emitted first, before any skirt ring), so this never alters the rendered surface.</param>
    /// <param name="orthoCoverage">Optional geographic placement of the larger ortho this tile is a sub-region of
    /// (Stage 2c). When set, the tile is TEXTURED through the existing ortho path — per-vertex geo-referenced UV +
    /// an <see cref="TerrainMesh3D.OrthoTileIndex"/> resolved from the tile centre's coverage cell — so it drapes
    /// the same ortho as the rest of the LOD scene. Null (default) renders the tile in hypsometric colour.</param>
    /// <param name="orthoTileIndexOffset">Added to the resolved ortho cell index so a baked tile's cell lines up
    /// with the renderer's ortho list (0 when baked tiles share the base scene's coverage grid). Ignored for a
    /// tile whose centre is outside the coverage (it stays hypsometric).</param>
    /// <returns>The meshed tile in the shared world frame, ready for the existing terrain shader.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tile"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="skirtDepthMeters"/> is negative, or the
    /// tile is too small (fewer than 2 columns or 2 rows) to mesh.</exception>
    /// <exception cref="ArgumentException">The tile exceeds the single-mesh vertex cap.</exception>
    public static TerrainMesh3D Build(
        BakedDemTile tile,
        GeoPoint projectionAnchor,
        TerrainMeshOptions? options = null,
        float skirtDepthMeters = 0f,
        OrthoCoverage? orthoCoverage = null,
        int orthoTileIndexOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(tile);
        ArgumentOutOfRangeException.ThrowIfNegative(skirtDepthMeters);
        if (tile.Columns < 2 || tile.Rows < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tile), $"A baked tile needs at least 2×2 samples to mesh (got {tile.Columns}×{tile.Rows}).");
        }

        TerrainMeshOptions meshOptions = MeshOptionsFor(options, skirtDepthMeters);
        DemRaster raster = AsRaster(tile);
        // tile.DetailRms (per-cell mid-frequency relief the downsample discarded) is row-major cols×rows, exactly
        // parallel to Heights, so it feeds the mesher's per-vertex detail with the same cell indexing. Null on the
        // finest level ⇒ per-vertex detail 0 (relief already in geometry). Faded by zoom (see
        // FadeDetailForZoom) so a coarse, far-picked tile doesn't out-bump a fine, near-picked one.
        return TerrainMesh3D.Build(raster, meshOptions, projectionAnchor, orthoCoverage, orthoTileIndexOffset, FadeDetailForZoom(tile));
    }

    /// <summary>
    /// Builds <paramref name="tile"/> as one OR MORE drawable meshes, CUT at ortho cell boundaries so a tile
    /// that straddles an ortho cell never clamps its far-side UV into relief-independent "strata" stripes
    /// (checklist §B.3). Each returned mesh covers one ortho cell sub-region of the tile and carries that cell's
    /// <see cref="TerrainMesh3D.OrthoTileIndex"/> + per-vertex geo-referenced UV — exactly as
    /// <see cref="TerrainMesh3D.BuildTiles"/> cuts the live LOD detail. When the tile lies inside a single ortho
    /// cell (or <paramref name="orthoCoverage"/> is null) this returns a single mesh identical to
    /// <see cref="Build"/>. Sub-block perimeters carry the same downward <paramref name="skirtDepthMeters"/>
    /// skirt, which only fills the vertical seam gap (the visible top surface is unchanged).
    /// </summary>
    /// <param name="tile">The baked DEM tile to mesh.</param>
    /// <param name="projectionAnchor">Shared world-frame origin (see <see cref="Build"/>).</param>
    /// <param name="options">Mesh tuning; null uses <see cref="TerrainMeshOptions"/> defaults.</param>
    /// <param name="skirtDepthMeters">Downward border skirt depth (see <see cref="Build"/>).</param>
    /// <param name="orthoCoverage">Ortho placement; when set, the tile is cut at this grid's cell boundaries.</param>
    /// <param name="orthoTileIndexOffset">Added to each sub-mesh's resolved ortho cell index.</param>
    /// <returns>One mesh per ortho cell the tile overlaps (at least one), all in the shared world frame.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tile"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="skirtDepthMeters"/> is negative, or the
    /// tile is too small (fewer than 2 columns or 2 rows) to mesh.</exception>
    public static IReadOnlyList<TerrainMesh3D> BuildCut(
        BakedDemTile tile,
        GeoPoint projectionAnchor,
        TerrainMeshOptions? options = null,
        float skirtDepthMeters = 0f,
        OrthoCoverage? orthoCoverage = null,
        int orthoTileIndexOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(tile);
        ArgumentOutOfRangeException.ThrowIfNegative(skirtDepthMeters);
        if (tile.Columns < 2 || tile.Rows < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tile), $"A baked tile needs at least 2×2 samples to mesh (got {tile.Columns}×{tile.Rows}).");
        }

        TerrainMeshOptions meshOptions = MeshOptionsFor(options, skirtDepthMeters);
        DemRaster raster = AsRaster(tile);
        float[]? detailGrid = FadeDetailForZoom(tile);

        // No ortho ⇒ no UV to clamp ⇒ a single block is correct (and cheapest). With ortho, route through
        // BuildTiles, which cuts at the coverage's cell boundaries (CutsWithCellBoundaries) so no sub-block
        // straddles a cell — the same anti-stripe cut the live detail/base meshing uses.
        if (orthoCoverage is null)
        {
            return new[] { TerrainMesh3D.Build(raster, meshOptions, projectionAnchor, orthoCoverage, orthoTileIndexOffset, detailGrid) };
        }

        // 32-bit indices, so a 256×256 baked tile (+ skirt) fits in ONE mesh — no sub-block split needed. Keep
        // maxTileSide at the full 256-sample side so BuildTiles cuts ONLY at ortho-cell boundaries (the anti-stripe
        // cut), not into 4 arbitrary draw-call-multiplying blocks. A tile fully inside one cell → a single mesh;
        // tile-to-tile edges are already welded by the baker, and any cell-boundary sub-block shares its source
        // heights with its neighbour so it doesn't crack. (Was a 128 split forced by the old 16-bit limit — the
        // ×4 draw-call cost that bound FPS; removing it is the P1 win.)
        const int maxTileSide = 255;
        return TerrainMesh3D.BuildTiles(
            raster, meshOptions, maxTileSide, orthoGridCols: 1, orthoGridRows: 1, projectionAnchor,
            edgeHeightSource: null, edgeMatchRows: 1, orthoCoverage, orthoTileIndexOffset, detailGrid);
    }

    // z13/z14/z15 (the only levels that carry DetailRms) grow coarser as the quadtree LOD selector picks them for
    // ground farther from the camera (z15=×2, z14=×4, z13=×8 relative to z16's native 1 m). Fade the synthetic
    // bump by that coarseness (TerrainMesh3D.DistanceFade) so a distant, heavily box-averaged tile doesn't show
    // MORE relief than a nearby fine one — the opposite of what the eye expects from an LOD system.
    private const float BakedDetailFadeHalfLife = 4f;

    private static float[]? FadeDetailForZoom(BakedDemTile tile)
    {
        if (tile.DetailRms is not { } rms)
        {
            return null;
        }

        float coarseness = 1 << Math.Max(0, 16 - tile.Zoom);
        float fade = TerrainMesh3D.DistanceFade(coarseness, BakedDetailFadeHalfLife);
        var faded = new float[rms.Length];
        for (int i = 0; i < rms.Length; i++)
        {
            faded[i] = rms[i] * fade;
        }

        return faded;
    }

    // Carries the caller's tuning but forces the skirt depth from the explicit argument (so callers don't juggle
    // two skirt settings) and keeps every other knob — exaggeration, light, ambient, normal radius — so a baked
    // tile shades exactly like the live terrain built with the same options.
    private static TerrainMeshOptions MeshOptionsFor(TerrainMeshOptions? options, float skirtDepthMeters)
    {
        options ??= new TerrainMeshOptions();
        return new TerrainMeshOptions
        {
            VerticalExaggeration = options.VerticalExaggeration,
            LightDirection = options.LightDirection,
            AmbientFactor = options.AmbientFactor,
            OverlayTintArgb = options.OverlayTintArgb,
            OverlayTintStrength = options.OverlayTintStrength,
            NormalSmoothingRadius = options.NormalSmoothingRadius,
            SkirtDepthMeters = skirtDepthMeters,
        };
    }

    /// <summary>
    /// Wraps a baked tile's repaired height grid as a <see cref="DemRaster"/> over the SAME geographic bounds,
    /// row/column convention (row 0 = north, column 0 = west) and NoData sentinel — so meshing the raster is
    /// meshing the baked tile. The heights array is shared (not copied): the mesher only reads it.
    /// </summary>
    /// <param name="tile">The baked tile to expose as a raster.</param>
    /// <returns>A raster view over <paramref name="tile"/> for full-resolution meshing.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tile"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The tile is smaller than the 2×2 a raster requires.</exception>
    public static DemRaster AsRaster(BakedDemTile tile)
    {
        ArgumentNullException.ThrowIfNull(tile);
        return new DemRaster(tile.Columns, tile.Rows, tile.Bounds, tile.Heights, (float)tile.NoDataValue);
    }
}