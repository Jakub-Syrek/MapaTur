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
    /// <param name="neighbourSource">Optional synchronous lookup of adjacent baked tiles. When set, the tile is
    /// meshed inside a one-cell halo of its neighbours' real heights (see <see cref="AsRasterWithHalo"/>) so its
    /// border normals are true centred differences that MATCH the neighbouring tile's — removing the shading line
    /// along the tile border. Look-only: no vertex moves. Null (default) meshes the tile as its own standalone
    /// raster, whose border normals clamp (today's behaviour).</param>
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
        int orthoTileIndexOffset = 0,
        Func<DemTileKey, BakedDemTile?>? neighbourSource = null)
    {
        ArgumentNullException.ThrowIfNull(tile);
        ArgumentOutOfRangeException.ThrowIfNegative(skirtDepthMeters);
        if (tile.Columns < 2 || tile.Rows < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tile), $"A baked tile needs at least 2×2 samples to mesh (got {tile.Columns}×{tile.Rows}).");
        }

        // tile.DetailRms (per-cell mid-frequency relief the downsample discarded) is row-major cols×rows, exactly
        // parallel to Heights, so it feeds the mesher's per-vertex detail with the same cell indexing. Null on the
        // finest level ⇒ per-vertex detail 0 (relief already in geometry). Faded by zoom (see
        // FadeDetailForZoom) so a coarse, far-picked tile doesn't out-bump a fine, near-picked one.
        (TerrainMeshOptions meshOptions, DemRaster raster, float[]? detailGrid, bool pooled) =
            PrepareFor(tile, options, skirtDepthMeters, neighbourSource);
        try
        {
            return TerrainMesh3D.Build(raster, meshOptions, projectionAnchor, orthoCoverage, orthoTileIndexOffset, detailGrid);
        }
        finally
        {
            ReturnScratch(raster, detailGrid, pooled);
        }
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
    /// <param name="neighbourSource">Optional synchronous lookup of adjacent baked tiles; when set, the tile meshes
    /// inside a one-cell neighbour halo so its border normals match the neighbouring tile's (see <see cref="Build"/>).</param>
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
        int orthoTileIndexOffset = 0,
        Func<DemTileKey, BakedDemTile?>? neighbourSource = null)
    {
        ArgumentNullException.ThrowIfNull(tile);
        ArgumentOutOfRangeException.ThrowIfNegative(skirtDepthMeters);
        if (tile.Columns < 2 || tile.Rows < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tile), $"A baked tile needs at least 2×2 samples to mesh (got {tile.Columns}×{tile.Rows}).");
        }

        (TerrainMeshOptions meshOptions, DemRaster raster, float[]? detailGrid, bool pooled) =
            PrepareFor(tile, options, skirtDepthMeters, neighbourSource);
        try
        {
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
        finally
        {
            ReturnScratch(raster, detailGrid, pooled);
        }
    }

    // The halo raster and the padded detail grid are BUILD SCRATCH: TerrainMesh3D copies what it needs into the
    // meshes and retains no reference to either (verified — its ctor stores only vertex/index arrays), so a
    // pooled build hands them back the moment the meshes exist. NEVER pooled without the halo: the un-haloed
    // AsRaster path SHARES tile.Heights with the RAM tile cache — returning that array would corrupt the cache.
    private static void ReturnScratch(DemRaster raster, float[]? detailGrid, bool pooled)
    {
        if (!pooled)
        {
            return;
        }

        MeshBufferPool.Shared.Return(raster.Samples);
        MeshBufferPool.Shared.Return(detailGrid);
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

    // Everything the halo decision touches, resolved once: the halo width, the (possibly haloed) raster, the
    // matching apron in the mesh options, and the detail grid padded to the same dimensions. No neighbour source
    // ⇒ no halo ⇒ no apron: the tile meshes as its own standalone raster, precisely as before. `Pooled` marks
    // that the raster/detail arrays are BUILD SCRATCH rented from the MeshBufferPool (a flight rebuilds dozens
    // of tiles per second; the z17 halo raster alone is ~553 KB — pure LOH churn when allocated per build) and
    // must go back via ReturnScratch once the meshes exist.
    private static (TerrainMeshOptions Options, DemRaster Raster, float[]? Detail, bool Pooled) PrepareFor(
        BakedDemTile tile,
        TerrainMeshOptions? options,
        float skirtDepthMeters,
        Func<DemTileKey, BakedDemTile?>? neighbourSource)
    {
        int haloCells = neighbourSource is null ? 0 : HaloCellsFor(tile, options);
        return (
            MeshOptionsFor(options, skirtDepthMeters, haloCells),
            haloCells > 0 ? BuildHaloRaster(tile, neighbourSource!, haloCells, rentScratch: true) : AsRaster(tile),
            haloCells > 0
                ? HaloDetail(FadeDetailForZoom(tile), tile.Columns, tile.Rows, haloCells, rentScratch: true)
                : FadeDetailForZoom(tile),
            haloCells > 0);
    }

    /// <summary>
    /// How many halo cells this tile's mesh passes need so NONE of them clamps at the tile border: the widest
    /// is the curvature-AO probe (45 m — at z17's ~0.78 m cells that is ~58 cells; a 1-cell halo fixes only the
    /// normals and leaves the MEASURED ±0.08 AO step on every border), then the micro-detail RMS window
    /// (±2 cells) and the normal radius. +1 on the AO reach absorbs the rounding difference between this
    /// tile-centre cell size and the mesh frame's anchor-latitude cell size. Clamped to what one ring of
    /// direct neighbours can supply (a full side minus the shared line).
    /// </summary>
    private static int HaloCellsFor(BakedDemTile tile, TerrainMeshOptions? options)
    {
        double centreLat = (tile.Bounds.SouthWest.Latitude + tile.Bounds.NorthEast.Latitude) / 2.0;
        double metersPerLon = TerrainMesh3D.MetersPerLatDegree * Math.Cos(centreLat * Math.PI / 180.0);
        double lonSpan = tile.Bounds.NorthEast.Longitude - tile.Bounds.SouthWest.Longitude;
        double cellWidthMeters = lonSpan / (tile.Columns - 1) * metersPerLon;
        int aoReach = cellWidthMeters > 0
            ? (int)Math.Round(TerrainCurvatureAo.MaxProbeRadiusMeters / cellWidthMeters) + 1
            : 1;
        int detailReach = TerrainMesh3D.NativeDetailWindowStep / 2;
        int normalReach = Math.Max(1, options?.NormalSmoothingRadius ?? 1);
        int reach = Math.Max(aoReach, Math.Max(detailReach, normalReach));
        return Math.Clamp(reach, 1, Math.Min(tile.Columns, tile.Rows) - 1);
    }

    // Carries the caller's tuning but forces the skirt depth from the explicit argument (so callers don't juggle
    // two skirt settings) and keeps every other knob — exaggeration, light, ambient, normal radius — so a baked
    // tile shades exactly like the live terrain built with the same options.
    private static TerrainMeshOptions MeshOptionsFor(TerrainMeshOptions? options, float skirtDepthMeters, int apronCells)
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
            NormalApronCells = apronCells,
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

    /// <summary>
    /// Wraps <paramref name="tile"/> as a raster GROWN by <paramref name="haloCells"/> rings of its neighbours'
    /// real heights, over bounds expanded by the same amount. Paired with
    /// <see cref="TerrainMeshOptions.NormalApronCells"/> = the same width (which withholds the rings from the
    /// output), it gives EVERY neighbourhood-sampling mesh pass real ground beyond the tile border instead of a
    /// clamp: the per-vertex normals (radius 1), the curvature-AO probe (metric rings to 45 m — ~58 cells at z17,
    /// where the clamp measured as a ±0.08 AO step along every border: the visible "tile grid / groove"), and the
    /// micro-detail RMS window (±2 cells). Both tiles sharing a border then derive the SAME shading at the shared,
    /// welded ground points, and the border line has nothing left to draw.
    ///
    /// Neighbours are addressed on the pixel-is-point convention the pyramid is baked on: adjacent tiles SHARE
    /// their boundary meridian/parallel (tile A's column <c>Columns-1</c> IS tile B's column 0, welded
    /// bit-identically by <c>DemTileBaker</c>), so the cell <c>j</c> beyond A's east edge is B's column <c>j</c>.
    ///
    /// Where there is nothing usable to borrow — the neighbour is absent (the pyramid's rim, a tile not baked) or
    /// holds NoData there (a coverage void, checklist §D) — the first ring reflects the tile's own edge slope
    /// outward (<c>2·edge − inner</c>, which reproduces the standalone clamp EXACTLY for the normals), and farther
    /// rings hold that value: finite, sentinel-free ground for the wider AO/detail windows, with no runaway linear
    /// extrapolation and no NoData for the normal loop to difference against.
    /// </summary>
    /// <param name="tile">The baked tile to expose as a haloed raster.</param>
    /// <param name="neighbourSource">Synchronous lookup for a neighbouring tile by slippy address; returns null when
    /// that tile isn't available. In production this is the LRU-cached baked-tile loader, so the eight lookups are
    /// RAM hits for any tile whose neighbours are also resident.</param>
    /// <param name="haloCells">Halo width in cells (≥ 1); clamped to what one ring of direct neighbours can supply
    /// (a full tile side minus the shared line).</param>
    /// <returns>A (Columns+2K)×(Rows+2K) raster: <paramref name="tile"/>'s grid inset by K cells, ringed by halo.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tile"/> or <paramref name="neighbourSource"/> is null.</exception>
    public static DemRaster AsRasterWithHalo(
        BakedDemTile tile, Func<DemTileKey, BakedDemTile?> neighbourSource, int haloCells = 1)
        => BuildHaloRaster(tile, neighbourSource, haloCells, rentScratch: false);

    // rentScratch: the mesh-build path rents the heights buffer from the MeshBufferPool (returned by
    // ReturnScratch right after the meshes exist); external callers (the virtual-tile synthesizer) get a plain
    // allocation they own outright. Every cell of the buffer is written below (interior copy + full ring scan),
    // so a dirty rented buffer needs no clearing.
    private static DemRaster BuildHaloRaster(
        BakedDemTile tile, Func<DemTileKey, BakedDemTile?> neighbourSource, int haloCells, bool rentScratch)
    {
        ArgumentNullException.ThrowIfNull(tile);
        ArgumentNullException.ThrowIfNull(neighbourSource);

        int cols = tile.Columns;
        int rows = tile.Rows;
        int k = Math.Clamp(haloCells, 1, Math.Min(cols, rows) - 1);
        int hCols = cols + (2 * k);
        int hRows = rows + (2 * k);
        float[] heights = rentScratch
            ? MeshBufferPool.Shared.RentSingle(hCols * hRows)
            : new float[hCols * hRows];

        // Interior: the tile's own grid, untouched, offset by the halo.
        for (int r = 0; r < rows; r++)
        {
            Array.Copy(tile.Heights, r * cols, heights, ((r + k) * hCols) + k, cols);
        }

        var neighbours = new Dictionary<(int Dx, int Dy), BakedDemTile?>();
        BakedDemTile? At(int dx, int dy)
        {
            if (!neighbours.TryGetValue((dx, dy), out BakedDemTile? n))
            {
                n = neighbourSource(new DemTileKey(tile.Zoom, tile.TileX + dx, tile.TileY + dy));
                neighbours[(dx, dy)] = n;
            }

            return n;
        }

        for (int hr = 0; hr < hRows; hr++)
        {
            int gr = hr - k; // tile-grid row; outside [0, rows-1] in the halo band
            bool rowInside = gr >= 0 && gr < rows;
            for (int hc = 0; hc < hCols; hc++)
            {
                int gc = hc - k;
                if (rowInside && gc >= 0 && gc < cols)
                {
                    hc = k + cols - 1; // interior span already copied — jump to the east halo band
                    continue;
                }

                // Which neighbour owns this cell, and where in ITS grid. Shared-edge arithmetic: our virtual
                // column cols-1+j is the east neighbour's column j (its column 0 IS our last column), hence
                // the (cols-1) shift — and symmetrically for west/rows/corners.
                int dx = gc < 0 ? -1 : gc >= cols ? 1 : 0;
                int dy = gr < 0 ? -1 : gr >= rows ? 1 : 0;
                float v = float.NaN;
                if (At(dx, dy) is { } n)
                {
                    int nc = gc - (dx * (cols - 1));
                    int nr = gr - (dy * (rows - 1));
                    if (nc >= 0 && nr >= 0 && nc < n.Columns && nr < n.Rows)
                    {
                        float h = n.Heights[(nr * n.Columns) + nc];
                        if (h != (float)n.NoDataValue)
                        {
                            v = h;
                        }
                    }
                }

                if (float.IsNaN(v))
                {
                    // Rim/NoData fallback: reflect the nearest edge cell's slope outward once, hold it beyond.
                    int ec = Math.Clamp(gc, 0, cols - 1);
                    int er = Math.Clamp(gr, 0, rows - 1);
                    int ic = ec - Math.Sign(gc - ec);
                    int ir = er - Math.Sign(gr - er);
                    v = Extrapolate(tile.Heights[(er * cols) + ec], tile.Heights[(ir * cols) + ic]);
                }

                heights[(hr * hCols) + hc] = v;
            }
        }

        return new DemRaster(hCols, hRows, ExpandByCells(tile.Bounds, cols, rows, k), heights, (float)tile.NoDataValue);
    }

    // Fallback halo height when there's no neighbour to borrow (pyramid rim, or a NoData void): mirror the tile's
    // own edge slope OUTWARD by reflecting the second line through the edge line (edge + (edge − inner) = 2·edge −
    // inner). This reproduces the standalone tile's clamped one-sided normal EXACTLY: the centred difference over
    // the two-cell halo baseline, (halo⁺ − halo⁻)/2Δ, collapses to (edge − inner)/Δ, which is the clamp. Replicating
    // the edge value instead would HALVE the gradient (same rise over twice the baseline) — a different, flatter
    // normal, i.e. a regression at every rim/void edge.
    private static float Extrapolate(float edge, float inner) => (2f * edge) - inner;

    // The halo raster spans `cells` extra cells on every side. Expanding symmetrically keeps the bounds CENTRE —
    // and so every interior vertex's world position — exactly where the un-haloed build put it.
    private static MapBounds ExpandByCells(MapBounds bounds, int cols, int rows, int cells)
    {
        double west = bounds.SouthWest.Longitude;
        double east = bounds.NorthEast.Longitude;
        double south = bounds.SouthWest.Latitude;
        double north = bounds.NorthEast.Latitude;
        double lonPitch = (east - west) / (cols - 1) * cells;
        double latPitch = (north - south) / (rows - 1) * cells;
        return new MapBounds(
            new GeoPoint(south - latPitch, west - lonPitch),
            new GeoPoint(north + latPitch, east + lonPitch));
    }

    // DetailRms is indexed with the RASTER's dimensions, so a haloed raster needs a haloed detail grid or every
    // lookup misaligns. The halo band's own values are NEVER read (the apron withholds those vertices from the
    // output), so a rented buffer's dirty band is harmless and zeros are only cosmetic on the allocating path.
    private static float[]? HaloDetail(float[]? detail, int cols, int rows, int haloCells, bool rentScratch = false)
    {
        if (detail is null)
        {
            return null;
        }

        int k = Math.Clamp(haloCells, 1, Math.Min(cols, rows) - 1);
        int hCols = cols + (2 * k);
        int length = hCols * (rows + (2 * k));
        float[] padded = rentScratch ? MeshBufferPool.Shared.RentSingle(length) : new float[length];
        for (int r = 0; r < rows; r++)
        {
            Array.Copy(detail, r * cols, padded, ((r + k) * hCols) + k, cols);
        }

        return padded;
    }
}