using System.Numerics;

using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Static 3D mesh built from a <see cref="DemRaster"/>. Stores world-space
/// vertex positions, per-vertex normals, per-vertex shaded ARGB colors and a
/// triangle index buffer. Coordinate convention: X east, Y north, Z up (right
/// handed). Origin = centre of the source raster's bounding box.
/// </summary>
public sealed class TerrainMesh3D
{
    // Internal (not private) so sibling builders (neighbour-halo sizing) use the same metric convention.
    internal const double MetersPerLatDegree = 111_320.0;

    /// <summary>Per-vertex world positions in metres.</summary>
    public Vector3[] Vertices { get; }

    /// <summary>Per-vertex unit normals.</summary>
    public Vector3[] Normals { get; }

    /// <summary>Per-vertex ARGB colours with per-vertex (Gouraud) Lambert shading baked in. Used by the Skia fallback renderer.</summary>
    public uint[] Colors { get; }

    /// <summary>Per-vertex unshaded hypsometric ARGB colours. The GPU renderer combines these with <see cref="Normals"/> to shade per-pixel.</summary>
    public uint[] BaseColors { get; }

    /// <summary>Per-vertex texture coordinates (2 floats u,v per vertex). With a 1×1 ortho grid they span the
    /// full raster (0,0)=NW..(1,1)=SE; with a finer grid they are LOCAL to this tile's ortho cell.</summary>
    public float[] TexCoords { get; }

    /// <summary>Per-vertex mid-frequency detail amplitude (metres RMS) — the sub-cell relief a coarsening
    /// downsample discarded for this vertex's cell. 0 on the finest level (relief is in the geometry) and on the
    /// live path. The GPU uses it to shade a coarse tile as if it still had those bumps (look-only; never geometry).</summary>
    public float[] Detail { get; }

    /// <summary>Index (row-major) of the ortho texture cell this mesh tile samples; 0 for a single full-extent ortho.</summary>
    public int OrthoTileIndex { get; }

    /// <summary>Triangle index buffer (3 uints per triangle). 32-bit indices so a single mesh can address
    /// more than 65 536 vertices (a full 256² baked tile plus its skirt) without splitting into sub-blocks.</summary>
    public uint[] Indices { get; }

    /// <summary>Centre of the source raster's bounding box in world metres (typically Vector3.Zero by construction).</summary>
    public Vector3 Center { get; }

    /// <summary>Approximate horizontal half-extent of the mesh in metres (max of half-width / half-height).</summary>
    public float HorizontalExtent { get; }

    /// <summary>Vertical exaggeration factor applied to elevations during build. Re-apply when projecting overlay geometry.</summary>
    public float VerticalExaggeration { get; }

    /// <summary>Source raster's geographic bounds.</summary>
    public MapBounds Bounds { get; }

    /// <summary>Unit direction toward the light used to bake <see cref="Colors"/>; the GPU shader reuses it so per-pixel shading matches the Skia fallback.</summary>
    public Vector3 LightDirection { get; }

    /// <summary>Ambient term used to bake <see cref="Colors"/>, in [0,1]; reused by the GPU shader.</summary>
    public float AmbientFactor { get; }

    /// <summary>Highest world-Z (already includes <see cref="VerticalExaggeration"/>) across this
    /// tile's vertices. Used by the controller to derive a camera safety floor so the camera can
    /// never drop below the surface or be zoomed inside a peak.</summary>
    public float MaxElevationZ { get; }

    /// <summary>Lowest world-Z across this tile's vertices.</summary>
    public float MinElevationZ { get; }

    /// <summary>World-space axis-aligned bounding box (min corner), computed once at construction so the renderer
    /// never has to re-scan every vertex per frame (the ortho-cell-bounds pass did, ~360 ms on a big tile set).</summary>
    public Vector3 WorldMin { get; }

    /// <summary>World-space axis-aligned bounding box (max corner). See <see cref="WorldMin"/>.</summary>
    public Vector3 WorldMax { get; }

    /// <summary>
    /// Approximate bytes this mesh's vertex + index data occupies once uploaded (positions + normals as
    /// <see cref="Vector3"/>, per-vertex shaded + base ARGB, UVs, and the <see cref="uint"/> index buffer).
    /// Used by the baked-tile streamer to cap RESIDENT geometry by total bytes, not just tile count — a broad
    /// ring of many small tiles and a deep ring of a few large ones cost very differently. The same layout is
    /// uploaded to the GPU, so this also tracks the resident VRAM geometry footprint closely enough to budget.
    /// </summary>
    public long EstimatedGpuBytes { get; }

    /// <summary>
    /// True when this mesh is the coarse BASE SKIN (the always-drawn smooth underlay beneath the streamed
    /// baked detail). The renderer discards base-skin fragments inside the <see cref="BaseCoverageMask"/> —
    /// on convex slopes the box-averaged base sits metres ABOVE the true z16 surface and would otherwise
    /// depth-bury the streamed detail. Set by the scene builder after the base tiles are built; false for
    /// every detail/baked mesh.
    /// </summary>
    public bool IsBaseSkin { get; set; }

    private TerrainMesh3D(
        Vector3[] vertices,
        Vector3[] normals,
        uint[] colors,
        uint[] baseColors,
        float[] texCoords,
        float[] detail,
        uint[] indices,
        Vector3 center,
        float horizontalExtent,
        float verticalExaggeration,
        MapBounds bounds,
        Vector3 lightDirection,
        float ambientFactor,
        int orthoTileIndex,
        GeoPoint projectionAnchor)
    {
        ProjectionAnchor = projectionAnchor;
        Vertices = vertices;
        Normals = normals;
        Colors = colors;
        BaseColors = baseColors;
        TexCoords = texCoords;
        Detail = detail;
        OrthoTileIndex = orthoTileIndex;
        Indices = indices;
        Center = center;
        HorizontalExtent = horizontalExtent;
        VerticalExaggeration = verticalExaggeration;
        Bounds = bounds;
        LightDirection = lightDirection;
        AmbientFactor = ambientFactor;

        // Compute the world AABB (and Z range) over vertices ONCE at construction so the host can ask without
        // iterating every frame. The ortho-cell-bounds pass re-scanned every vertex of every tile on each detail
        // swap (~360 ms on a ~760-tile set) — now it just reads WorldMin/WorldMax. Empty meshes report Center.
        float minX = float.PositiveInfinity, minY = float.PositiveInfinity, minZ = float.PositiveInfinity;
        float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity, maxZ = float.NegativeInfinity;
        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 v = vertices[i];
            if (v.X < minX) { minX = v.X; }
            if (v.X > maxX) { maxX = v.X; }
            if (v.Y < minY) { minY = v.Y; }
            if (v.Y > maxY) { maxY = v.Y; }
            if (v.Z < minZ) { minZ = v.Z; }
            if (v.Z > maxZ) { maxZ = v.Z; }
        }

        MinElevationZ = float.IsPositiveInfinity(minZ) ? 0f : minZ;
        MaxElevationZ = float.IsNegativeInfinity(maxZ) ? 0f : maxZ;
        WorldMin = vertices.Length > 0 ? new Vector3(minX, minY, minZ) : center;
        WorldMax = vertices.Length > 0 ? new Vector3(maxX, maxY, maxZ) : center;

        // Per-vertex: position (12) + normal (12) + colour (4) + base colour (4) + UV (8) + detail (4) = 44 bytes;
        // per index: 4 bytes (32-bit). Skirt vertices are already included in the arrays by this point.
        EstimatedGpuBytes = ((long)vertices.Length * 44L) + ((long)indices.Length * 4L);
    }

    /// <summary>
    /// Returns this tile's UPLOAD-ONLY attribute buffers to the <see cref="MeshBufferPool"/> for reuse — call
    /// right after the renderer's GL upload, their only reader. OWNERSHIP CONTRACT (2026-07-16): NEVER return
    /// <see cref="Vertices"/>, <see cref="Colors"/> or <see cref="Indices"/> — they stay CPU-readable for the
    /// tile's whole residency (dragon-perch elevation sampling reads Vertices+Indices; the Skia fallback
    /// renderer reads Vertices+Colors+Indices; Terrain3DProjection reads Vertices). Returning them lets a later
    /// rent overwrite a RESIDENT tile's geometry underneath those readers — silent wrong elevations. That latent
    /// bug shipped masked only because the skirt's Array.Resize kept the pool from ever re-renting; with the
    /// rent/return cycle live (buffers pre-sized for the skirt), this contract is what keeps it safe.
    /// </summary>
    public void ReturnBuffersToPool()
    {
        MeshBufferPool.Shared.Return(Normals);
        MeshBufferPool.Shared.Return(BaseColors);
        MeshBufferPool.Shared.Return(TexCoords);
        MeshBufferPool.Shared.Return(Detail);
    }

    /// <summary>
    /// Projects a geographic point + raw elevation into the mesh's world-space coordinate system
    /// (X east, Y north, Z up). Elevation is scaled by <see cref="VerticalExaggeration"/> so overlay
    /// geometry (trails, routes) sits at the same vertical scale as the rendered terrain.
    /// </summary>
    public Vector3 GeoToWorld(GeoPoint geoPoint, float elevationMeters)
        => LocalTangentProjection.GeoToWorld(geoPoint, elevationMeters, ProjectionAnchor, VerticalExaggeration);

    /// <summary>
    /// Inverse of <see cref="GeoToWorld"/>: maps a world-space point (X east, Y north, in metres)
    /// back to its geographic position. The Z/elevation component is ignored — only the planar
    /// XY position determines longitude/latitude. Used to translate a 3D camera focus point into a
    /// 2D map centre so switching between 3D and 2D keeps the same place framed.
    /// </summary>
    public GeoPoint WorldToGeo(Vector3 worldPoint)
        => LocalTangentProjection.WorldToGeo(worldPoint, ProjectionAnchor);

    /// <summary>
    /// Geographic origin of this mesh's world frame. The raster's own bounds centre unless a shared anchor
    /// was passed to <see cref="BuildTiles"/> (LOD) — then it's that anchor, so independently-built windows
    /// project into one frame and the camera doesn't jump as tiles stream.
    /// </summary>
    public GeoPoint ProjectionAnchor { get; }

    /// <summary>Upper bound on vertices in a single mesh. Indices are 32-bit (uint), so the real ceiling is
    /// the array limit; this generous cap (2048²) just guards the single-block <c>Build</c> against a runaway
    /// full-raster mesh and steers huge rasters to <see cref="BuildTiles"/>. A 256² baked tile + skirt (~66 556 verts) fits
    /// far under it — the old 65 536 (16-bit) limit is gone, so a baked tile no longer needs splitting.</summary>
    private const int MaxVerticesPerMesh = 2048 * 2048;

    /// <summary>
    /// The ortho texture cell a mesh tile samples: its span in raster-grid indices (so per-vertex UV is
    /// local to the cell) and its index in the ortho grid. Default (<see cref="Spans"/> false) means
    /// "one texture over the whole raster" — the legacy single-image behaviour.
    /// </summary>
    private readonly record struct OrthoCell(int ColStart, int ColEnd, int RowStart, int RowEnd, int TileIndex, bool Spans)
    {
        public static OrthoCell Full(int cols, int rows) => new(0, cols - 1, 0, rows - 1, 0, true);
    }

    /// <summary>
    /// Returns <paramref name="parts"/>+1 grid-index boundaries splitting the INCLUSIVE range
    /// [<paramref name="first"/>, <paramref name="last"/>] into contiguous ranges that share their seam index with
    /// the next range (so adjacent meshes have no crack). The range is the raster's drawn interior, which is the
    /// whole raster unless a normal apron withholds a halo ring from the output.
    /// </summary>
    private static int[] SplitPoints(int first, int last, int parts)
    {
        var splits = new int[parts + 1];
        for (int i = 0; i <= parts; i++)
        {
            splits[i] = first + (int)Math.Round((double)(last - first) * i / parts);
        }
        return splits;
    }

    /// <summary>
    /// Absolute cell indices sampled along an axis: <paramref name="start"/>, +step, …, ALWAYS ending exactly at
    /// <paramref name="end"/> (the shared boundary, so adjacent strided blocks meet on the absolute grid). step ≤ 1
    /// returns every cell. Used by <see cref="BuildAdaptiveTiles"/> so a coarse tile's vertices land on the same
    /// world grid as a finer neighbour's — shared edges, no gap.
    /// </summary>
    private static int[] SampleAxis(int start, int end, int step)
    {
        if (step <= 1)
        {
            var all = new int[end - start + 1];
            for (int i = 0; i < all.Length; i++)
            {
                all[i] = start + i;
            }

            return all;
        }

        var list = new List<int>(((end - start) / step) + 2);
        for (int v = start; v < end; v += step)
        {
            list.Add(v);
        }

        list.Add(end); // boundary cell is always the last sample → shared with the neighbour, no gap
        return list.ToArray();
    }

    /// <summary>
    /// Maps a tile-local edge vertex (<paramref name="lr"/>,<paramref name="lc"/>) to its flat index, WELDING a
    /// vertex on a decimated edge to the nearest anchor (anchor spacing = the coarser neighbour's step ratio
    /// rN/rS/rW/rE) so a finer tile's shared edge uses only the vertices the coarser neighbour also has — the
    /// chunked-LOD T-junction fix. ratio ≤ 1 = no change; corners (index 0 / last) are always anchors.
    /// </summary>
    private static uint WeldEdgeVertex(int lr, int lc, int tileCols, int tileRows, int rN, int rS, int rW, int rE)
    {
        static int Anchor(int idx, int ratio, int count)
        {
            if (ratio <= 1)
            {
                return idx;
            }

            int lo = (idx / ratio) * ratio;
            int hi = Math.Min(lo + ratio, count - 1);
            return (idx - lo) <= (hi - idx) ? lo : hi;
        }

        if (lr == 0 && rN > 1)
        {
            lc = Anchor(lc, rN, tileCols);
        }
        else if (lr == tileRows - 1 && rS > 1)
        {
            lc = Anchor(lc, rS, tileCols);
        }

        if (lc == 0 && rW > 1)
        {
            lr = Anchor(lr, rW, tileRows);
        }
        else if (lc == tileCols - 1 && rE > 1)
        {
            lr = Anchor(lr, rE, tileRows);
        }

        return (uint)((lr * tileCols) + lc);
    }

    /// <summary>
    /// Builds a single terrain mesh from a DEM raster. Indices are 32-bit, so a single mesh can hold a
    /// full-resolution baked tile; only an absurdly large raster (the guard cap) must use <see cref="BuildTiles"/>.
    /// </summary>
    /// <param name="raster">Source DEM.</param>
    /// <param name="options">Optional tuning; default options use NW sun at 2× vertical exaggeration.</param>
    public static TerrainMesh3D Build(DemRaster raster, TerrainMeshOptions? options = null)
        => Build(raster, options, projectionAnchor: null);

    /// <summary>
    /// Builds a single terrain mesh from a DEM raster, optionally about a SHARED world-frame origin so the
    /// result lines up with other independently-built meshes (the LOD scene). Indices are 32-bit, so only an
    /// absurdly large raster (the guard cap) must use <see cref="BuildTiles"/>.
    /// </summary>
    /// <param name="raster">Source DEM.</param>
    /// <param name="options">Optional tuning; default options use NW sun at 2× vertical exaggeration.</param>
    /// <param name="projectionAnchor">Optional fixed world-frame origin. Null (default) anchors on the raster's
    /// own bounds centre (vertices centred on ~0, legacy <see cref="Build(DemRaster, TerrainMeshOptions)"/>
    /// behaviour). A shared anchor across independently-built single-tile meshes gives them one world frame, so
    /// each tile lands exactly where <see cref="LocalTangentProjection.GeoToWorld"/> would put the same ground
    /// point — used to mesh baked tiles into the streaming LOD frame.</param>
    public static TerrainMesh3D Build(DemRaster raster, TerrainMeshOptions? options, GeoPoint? projectionAnchor)
        => Build(raster, options, projectionAnchor, orthoCoverage: null, orthoTileIndexOffset: 0);

    /// <summary>
    /// Builds a single terrain mesh about a shared anchor, optionally TEXTURED through a larger ortho this raster
    /// is a sub-region of. Identical to <see cref="Build(DemRaster, TerrainMeshOptions, GeoPoint?)"/> except that
    /// when <paramref name="orthoCoverage"/> is set the vertices get geo-referenced ortho UVs and the mesh's
    /// <see cref="OrthoTileIndex"/> is resolved from its centre's position in the coverage — so a single baked
    /// tile drapes the same ortho as the rest of the LOD scene (Stage 2c) instead of falling back to hypsometric.
    /// </summary>
    /// <param name="raster">Source DEM (≤ 65 536 cells).</param>
    /// <param name="options">Optional tuning; null uses defaults.</param>
    /// <param name="projectionAnchor">Optional shared world-frame origin (see the sibling overload).</param>
    /// <param name="orthoCoverage">Optional ortho placement for geo-referenced UV + per-tile cell selection. Null
    /// (default) keeps the legacy local UV and the full-extent ortho cell index.</param>
    /// <param name="orthoTileIndexOffset">Added to the resolved ortho cell index so this mesh's cell lines up with
    /// the renderer's ortho list. Ignored for an out-of-coverage tile (which stays hypsometric). 0 = no shift.</param>
    /// <param name="detailGrid">Optional per-cell mid-frequency detail amplitude (metres RMS), row-major and the
    /// SAME dimensions/indexing as <paramref name="raster"/>. Copied into each vertex's <see cref="Detail"/>; null
    /// (default) leaves all detail 0. Used only for look-only coarse-tile shading, never geometry.</param>
    /// <exception cref="ArgumentNullException"><paramref name="raster"/> is null.</exception>
    /// <exception cref="ArgumentException">The raster exceeds the single-mesh vertex cap (use <see cref="BuildTiles"/>).</exception>
    public static TerrainMesh3D Build(
        DemRaster raster,
        TerrainMeshOptions? options,
        GeoPoint? projectionAnchor,
        OrthoCoverage? orthoCoverage,
        int orthoTileIndexOffset,
        float[]? detailGrid = null)
    {
        ArgumentNullException.ThrowIfNull(raster);
        options ??= new TerrainMeshOptions();

        // Indices are 32-bit, so a single mesh can address far more than 65 536 vertices; this only rejects an
        // absurdly large single Build (guard cap) and steers it to the tiled path.
        if ((long)raster.Columns * raster.Rows > MaxVerticesPerMesh)
        {
            throw new ArgumentException(
                "DEM raster is too large for a single mesh; use BuildTiles for high-resolution rasters.",
                nameof(raster));
        }

        // World-frame origin: the raster's own bounds centre by default (vertices centred on ~0), or a shared
        // anchor. anchorOffset is where this raster's centre sits in the anchored frame (Zero when anchor == own
        // centre), added to every vertex; the frame's lon scale uses the anchor latitude so vertices and
        // GeoToWorld agree exactly — identical to the BuildTiles anchoring path.
        var rasterCentre = new GeoPoint(
            (raster.North + raster.South) / 2.0, (raster.East + raster.West) / 2.0);
        GeoPoint anchor = projectionAnchor ?? rasterCentre;
        Vector3 anchorOffset = LocalTangentProjection.GeoToWorld(rasterCentre, 0f, anchor, 1f);
        MeshFrame frame = ComputeFrame(raster, anchor.Latitude);

        // Withhold the apron ring from the output (it exists only so the normal loop below has real neighbours at
        // the drawn region's edge instead of clamping). The frame stays computed over the WHOLE raster: the halo is
        // symmetric, so the centre — and therefore every interior vertex's world position — is unchanged.
        int apron = ApronFor(raster, options);
        return BuildBlock(
            raster, options, frame, apron, raster.Columns - 1 - apron, apron, raster.Rows - 1 - apron,
            anchor, anchorOffset, orthoCell: InteriorCell(raster, apron),
            orthoCoverage: orthoCoverage, orthoTileIndexOffset: orthoTileIndexOffset, detailGrid: detailGrid,
            emitBounds: InteriorBounds(raster, apron));
    }

    /// <summary>
    /// Validated apron width for <paramref name="raster"/>: the ring of cells read for normals but not drawn.
    /// </summary>
    /// <exception cref="ArgumentException">The apron would leave fewer than 2 interior cells on an axis.</exception>
    private static int ApronFor(DemRaster raster, TerrainMeshOptions options)
    {
        int apron = Math.Max(0, options.NormalApronCells);
        if (apron == 0)
        {
            return 0;
        }

        if (raster.Columns - (2 * apron) < 2 || raster.Rows - (2 * apron) < 2)
        {
            throw new ArgumentException(
                $"A normal apron of {apron} cell(s) leaves no meshable interior in a {raster.Columns}×{raster.Rows} "
                + "raster (needs at least 2 interior cells per axis).",
                nameof(raster));
        }

        return apron;
    }

    /// <summary>
    /// The geographic extent of the DRAWN region — the raster inset by <paramref name="apron"/> cells. The mesh must
    /// report this rather than the halo's bounds: the halo is another tile's ground, and a consumer that trusts
    /// Bounds (ortho placement, coverage reasoning) would otherwise think this mesh owns it.
    /// </summary>
    private static MapBounds InteriorBounds(DemRaster raster, int apron)
    {
        if (apron == 0)
        {
            return raster.Bounds;
        }

        double lonPitch = (raster.East - raster.West) / (raster.Columns - 1);
        double latPitch = (raster.North - raster.South) / (raster.Rows - 1);
        return new MapBounds(
            new GeoPoint(raster.South + (latPitch * apron), raster.West + (lonPitch * apron)),
            new GeoPoint(raster.North - (latPitch * apron), raster.East - (lonPitch * apron)));
    }

    // The legacy full-extent ortho cell spans "the whole raster" — which, with a halo, is more ground than this mesh
    // draws. Span the INTERIOR instead so local UV still runs 0→1 across the drawn surface exactly as it does today.
    private static OrthoCell InteriorCell(DemRaster raster, int apron)
        => apron == 0
            ? default
            : new OrthoCell(apron, raster.Columns - 1 - apron, apron, raster.Rows - 1 - apron, 0, true);

    /// <summary>
    /// Builds a high-resolution terrain as a set of mesh tiles, each bounded by <paramref name="maxTileSide"/>, so a
    /// DEM far larger than a single tile can be rendered at full detail (one <c>SKVertices</c> per tile).
    /// Every tile is expressed in the SAME world frame (origin = full-raster centre) and carries the full
    /// raster's <see cref="Bounds"/> / <see cref="HorizontalExtent"/>, so overlays projecting against any
    /// tile share one consistent coordinate system. Adjacent tiles share their seam row/column of
    /// vertices (no cracks), and normals are computed from the full raster's neighbours so shading is
    /// continuous across tile seams.
    /// </summary>
    /// <param name="raster">Source DEM (may exceed 65 536 cells).</param>
    /// <param name="options">Optional tuning; default options use NW sun at 2× vertical exaggeration.</param>
    /// <param name="maxTileSide">Maximum vertices per tile side minus one; a tile spans up to (maxTileSide + 1)² vertices. Default 255 → ≤ 256² = 65 536.</param>
    /// <param name="orthoGridCols">Number of ortho texture cells across (west–east). 1 = a single texture over the whole raster.</param>
    /// <param name="orthoGridRows">Number of ortho texture cells down (north–south). 1 = a single texture over the whole raster.</param>
    /// <param name="projectionAnchor">Optional fixed world-frame origin. Null (default) anchors on the raster's
    /// own bounds centre (legacy behaviour). A shared anchor across independently-built windows gives them one
    /// world frame so a streaming reload doesn't shift the origin under a fixed camera (LOD).</param>
    /// <param name="edgeHeightSource">Optional coarser raster (e.g. the LOD base) to morph this raster's OUTER
    /// band of vertices toward (edge matching, Krok 4c). Null (default) keeps every vertex at its own height.
    /// When set, the outermost <paramref name="edgeMatchRows"/> rows blend from the source's bilinear height
    /// at the very edge to full detail one band in, so a streamed detail patch melts into the base instead of
    /// stepping; no-data source samples leave the detail height.</param>
    /// <param name="edgeMatchRows">Width (in vertices) of the edge-match morph band. 1 (default) pins only the
    /// perimeter; higher blends the step over several rows. Ignored when <paramref name="edgeHeightSource"/> is null.</param>
    /// <param name="orthoCoverage">Optional geographic placement of a larger ortho this raster is a sub-region
    /// of (ortho-on-LOD). When set, per-vertex UV is geo-referenced into the coverage and each tile picks its
    /// ortho cell by its centre's geo position (MVP: one cell per tile). Null (default) = legacy local UV.</param>
    /// <param name="orthoTileIndexOffset">Added to every tile's ortho cell index so this mesh's cells sit
    /// AFTER another set in the renderer's flat ortho list (e.g. a high-zoom near-field patch appended past
    /// the base scene's cells). 0 (default) = no shift.</param>
    /// <param name="detailGrid">Optional per-cell mid-frequency detail amplitude (metres RMS), row-major and the
    /// SAME dimensions/indexing as <paramref name="raster"/>; copied into each vertex's <see cref="Detail"/>. Null
    /// (default) leaves detail 0.</param>
    public static IReadOnlyList<TerrainMesh3D> BuildTiles(
        DemRaster raster,
        TerrainMeshOptions? options = null,
        int maxTileSide = 255,
        int orthoGridCols = 1,
        int orthoGridRows = 1,
        GeoPoint? projectionAnchor = null,
        DemRaster? edgeHeightSource = null,
        int edgeMatchRows = 1,
        OrthoCoverage? orthoCoverage = null,
        int orthoTileIndexOffset = 0,
        float[]? detailGrid = null)
    {
        ArgumentNullException.ThrowIfNull(raster);
        if (maxTileSide < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTileSide), maxTileSide, "maxTileSide must be at least 1.");
        }

        if ((maxTileSide + 1L) * (maxTileSide + 1L) > MaxVerticesPerMesh)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxTileSide), maxTileSide, "A tile of this side would exceed the single-mesh vertex cap.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(orthoGridCols, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(orthoGridRows, 1);

        options ??= new TerrainMeshOptions();

        // World-frame origin: the raster's own bounds centre by default (vertices centred on ~0), or a
        // caller-supplied anchor shared with other windows. anchorOffset is where this raster's centre sits
        // in the anchored frame (Zero when anchor == own centre), added to every vertex; the frame's lon
        // scale uses the anchor latitude so vertices and GeoToWorld agree exactly.
        var rasterCentre = new GeoPoint(
            (raster.North + raster.South) / 2.0, (raster.East + raster.West) / 2.0);
        GeoPoint anchor = projectionAnchor ?? rasterCentre;
        Vector3 anchorOffset = LocalTangentProjection.GeoToWorld(rasterCentre, 0f, anchor, 1f);

        MeshFrame frame = ComputeFrame(raster, anchor.Latitude);
        int cols = raster.Columns;
        int rows = raster.Rows;

        // Halo ring (read for normals, never drawn) — see TerrainMeshOptions.NormalApronCells. The cut planning
        // below runs over the INTERIOR only; BuildBlock's normal loop still samples the full raster, so a block
        // sitting on the interior's edge reads the halo's real cells instead of clamping.
        int apron = ApronFor(raster, options);
        int colFirst = apron;
        int colLast = cols - 1 - apron;
        int rowFirst = apron;
        int rowLast = rows - 1 - apron;
        MapBounds emitBounds = InteriorBounds(raster, apron);

        var tiles = new List<TerrainMesh3D>();

        // Geo-referenced ortho (ortho-on-LOD): this raster is a SUB-REGION of a larger ortho, so UV is mapped
        // by geographic position into the coverage (not by the orthoGrid split, which assumes ortho-extent ==
        // raster-extent). Tile by maxTileSide and let each block pick its ortho cell + per-vertex geo-UV from
        // the coverage (MVP: one cell per block, by the block centre — no per-cell cutting yet).
        if (orthoCoverage is not null)
        {
            // Cut the raster at the ortho CELL boundaries as well as the maxTileSide grid, so a block never
            // STRADDLES a cell boundary. A straddling block picks one cell by its centre and clamps the UV of
            // its far-side vertices — stretching that cell's edge row of texels into long parallel "strata"
            // stripes (independent of relief, along the cell-grid line). Keeping every block inside one cell
            // makes its per-vertex UV stay in [0,1] (no clamp). Blocks fully outside coverage still resolve to
            // hypsometric via the centre test in BuildBlock.
            double covW = orthoCoverage.Bounds.SouthWest.Longitude;
            double covE = orthoCoverage.Bounds.NorthEast.Longitude;
            double covS = orthoCoverage.Bounds.SouthWest.Latitude;
            double covN = orthoCoverage.Bounds.NorthEast.Latitude;
            var colBoundaries = new SortedSet<int>();
            for (int i = 1; i < orthoCoverage.GridCols; i++)
            {
                double lon = covW + (i * (covE - covW) / orthoCoverage.GridCols);
                int col = (int)Math.Round((lon - raster.West) / (raster.East - raster.West) * (cols - 1));
                if (col > 0 && col < cols - 1)
                {
                    colBoundaries.Add(col);
                }
            }

            var rowBoundaries = new SortedSet<int>();
            for (int i = 1; i < orthoCoverage.GridRows; i++)
            {
                double lat = covN - (i * (covN - covS) / orthoCoverage.GridRows);
                int row = (int)Math.Round((raster.North - lat) / (raster.North - raster.South) * (rows - 1));
                if (row > 0 && row < rows - 1)
                {
                    rowBoundaries.Add(row);
                }
            }

            int[] colCuts = CutsWithCellBoundaries(colFirst, colLast, maxTileSide, colBoundaries);
            int[] rowCuts = CutsWithCellBoundaries(rowFirst, rowLast, maxTileSide, rowBoundaries);
            for (int ri = 0; ri < rowCuts.Length - 1; ri++)
            {
                int r0 = rowCuts[ri];
                int r1 = rowCuts[ri + 1];
                for (int ci = 0; ci < colCuts.Length - 1; ci++)
                {
                    tiles.Add(BuildBlock(
                        raster, options, frame, colCuts[ci], colCuts[ci + 1], r0, r1, anchor, anchorOffset,
                        OrthoCell.Full(cols, rows), edgeHeightSource, edgeMatchRows, orthoCoverage, orthoTileIndexOffset,
                        detailGrid: detailGrid, emitBounds: emitBounds));
                }
            }

            return tiles;
        }

        // Tile per ortho cell so a mesh tile never straddles a texture boundary: each mesh tile samples one
        // ortho cell with UV local to that cell. Cells share their seam row/column with the neighbour, and
        // mesh sub-tiles use inclusive ranges, so there are no cracks and textures meet seamlessly under
        // ClampToEdge. orthoGrid 1×1 → one cell over the whole raster (the legacy single-texture path).
        int[] colSplits = SplitPoints(colFirst, colLast, orthoGridCols);
        int[] rowSplits = SplitPoints(rowFirst, rowLast, orthoGridRows);

        for (int gy = 0; gy < orthoGridRows; gy++)
        {
            int cellR0 = rowSplits[gy];
            int cellR1 = rowSplits[gy + 1];
            for (int gx = 0; gx < orthoGridCols; gx++)
            {
                int cellC0 = colSplits[gx];
                int cellC1 = colSplits[gx + 1];
                var cell = new OrthoCell(cellC0, cellC1, cellR0, cellR1, (gy * orthoGridCols) + gx, true);

                for (int r0 = cellR0; r0 < cellR1; r0 += maxTileSide)
                {
                    int r1 = Math.Min(r0 + maxTileSide, cellR1);
                    for (int c0 = cellC0; c0 < cellC1; c0 += maxTileSide)
                    {
                        int c1 = Math.Min(c0 + maxTileSide, cellC1);
                        tiles.Add(BuildBlock(raster, options, frame, c0, c1, r0, r1, anchor, anchorOffset, cell, edgeHeightSource, edgeMatchRows, orthoTileIndexOffset: orthoTileIndexOffset, detailGrid: detailGrid, emitBounds: emitBounds));
                    }
                }
            }
        }

        return tiles;
    }

    /// <summary>Camera-independent world-frame parameters shared by every tile of a raster.</summary>
    private readonly record struct MeshFrame(
        double MetersPerLonDegree,
        double HalfWidthMeters,
        double HalfHeightMeters,
        double CellWidthMeters,
        double CellHeightMeters,
        float HorizontalExtent);

    private static MeshFrame ComputeFrame(DemRaster raster, double projectionLatitude)
    {
        int cols = raster.Columns;
        int rows = raster.Rows;
        double metersPerLonDegree = MetersPerLatDegree * Math.Cos(projectionLatitude * Math.PI / 180.0);
        double halfWidthMeters = (raster.East - raster.West) * 0.5 * metersPerLonDegree;
        double halfHeightMeters = (raster.North - raster.South) * 0.5 * MetersPerLatDegree;
        double cellWidthMeters = (cols > 1) ? ((raster.East - raster.West) / (cols - 1)) * metersPerLonDegree : 1.0;
        double cellHeightMeters = (rows > 1) ? ((raster.North - raster.South) / (rows - 1)) * MetersPerLatDegree : 1.0;
        float horizontalExtent = (float)Math.Max(halfWidthMeters, halfHeightMeters);
        return new MeshFrame(metersPerLonDegree, halfWidthMeters, halfHeightMeters, cellWidthMeters, cellHeightMeters, horizontalExtent);
    }

    /// <summary>
    /// Builds one mesh covering the inclusive raster sub-window [colStart..colEnd] × [rowStart..rowEnd],
    /// positioned in the full-raster world frame. Normals sample the full raster (clamped at its edges),
    /// so a tile's interior-seam normals match the neighbouring tile's.
    /// </summary>
    /// <summary>
    /// Builds the per-tile adaptive detail (Model 1 roughness LOD) as CRACK-FREE chunked-LOD tiles. Unlike an
    /// independent crop + per-crop subsample (whose strided edges land at different world positions → black
    /// see-through cracks between different-step tiles), every tile here is sampled straight from the SAME full
    /// <paramref name="raster"/> at its own <c>SubsampleStep</c> on the ABSOLUTE grid, so neighbours share their
    /// boundary vertices exactly; where a finer tile borders a coarser one its shared edge is welded to the
    /// coarser step (no T-junction), and normals come from the full raster so shading is continuous across seams.
    /// </summary>
    /// <param name="raster">The full loaded detail window (NoData/holes already applied).</param>
    /// <param name="plan">Per-tile decisions from <see cref="PerTileDetailPlanner"/> (inclusive ranges + step + edge steps).</param>
    /// <param name="options">Mesh tuning; null = defaults.</param>
    /// <param name="projectionAnchor">Shared world-frame origin (the LOD scene anchor).</param>
    /// <param name="orthoCoverage">Optional ortho coverage for geo-referenced UV.</param>
    /// <param name="orthoTileIndexOffset">Added to each tile's ortho cell index (appended past the base cells).</param>
    /// <param name="edgeHeightSource">Optional coarse base to morph the WINDOW-perimeter band toward (the
    /// distance-from-edge test uses full-raster coordinates, so only blocks touching the window edge morph —
    /// internal tile seams keep full detail). Without it the patch boundary steps against a coarse base
    /// (displaced silhouette = a "duplicated ridge").</param>
    /// <param name="edgeMatchRows">Width (in raster cells) of the perimeter morph band; 1 pins only the edge.</param>
    /// <param name="cache">Optional per-block mesh cache: an unchanged block (same key) is reused, not rebuilt,
    /// so the renderer uploads only the delta across detail reloads. Null = legacy (always fresh objects).</param>
    /// <param name="absColOrigin">Absolute z16-pixel column of the window's cell (0,0); makes cache keys stable across moves.</param>
    /// <param name="absRowOrigin">Absolute z16-pixel row of the window's cell (0,0); makes cache keys stable across moves.</param>
    public static IReadOnlyList<TerrainMesh3D> BuildAdaptiveTiles(
        DemRaster raster,
        IReadOnlyList<PerTileLodDecision> plan,
        TerrainMeshOptions? options = null,
        GeoPoint? projectionAnchor = null,
        OrthoCoverage? orthoCoverage = null,
        int orthoTileIndexOffset = 0,
        DemRaster? edgeHeightSource = null,
        int edgeMatchRows = 1,
        PerTileMeshCache? cache = null,
        int absColOrigin = 0,
        int absRowOrigin = 0)
    {
        ArgumentNullException.ThrowIfNull(raster);
        ArgumentNullException.ThrowIfNull(plan);
        options ??= new TerrainMeshOptions();

        var rasterCentre = new GeoPoint((raster.North + raster.South) / 2.0, (raster.East + raster.West) / 2.0);
        GeoPoint anchor = projectionAnchor ?? rasterCentre;
        Vector3 anchorOffset = LocalTangentProjection.GeoToWorld(rasterCentre, 0f, anchor, 1f);
        MeshFrame frame = ComputeFrame(raster, anchor.Latitude);

        const int maxTileSide = 250; // ≤ 250 sampled verts/side keeps each live-detail block a manageable size

        // Ortho cell boundaries (absolute raster cols/rows). A block that STRADDLES a cell boundary picks one
        // cell by its centre and clamps the far-side UV → that cell's edge texels stretch into relief-independent
        // "strata" stripes. BuildTiles' orthoCoverage path already cuts at these boundaries (197cde6); the LOD
        // base goes through BuildAdaptiveTiles, which did NOT — so it needs the same cut to kill the stripes.
        var cellCols = new SortedSet<int>();
        var cellRows = new SortedSet<int>();
        if (orthoCoverage is { } cov)
        {
            double cw = cov.Bounds.SouthWest.Longitude, ce = cov.Bounds.NorthEast.Longitude;
            double cs = cov.Bounds.SouthWest.Latitude, cn = cov.Bounds.NorthEast.Latitude;
            for (int i = 1; i < cov.GridCols; i++)
            {
                double lon = cw + (i * (ce - cw) / cov.GridCols);
                int col = (int)Math.Round((lon - raster.West) / (raster.East - raster.West) * (raster.Columns - 1));
                if (col > 0 && col < raster.Columns - 1)
                {
                    cellCols.Add(col);
                }
            }

            for (int i = 1; i < cov.GridRows; i++)
            {
                double lat = cn - (i * (cn - cs) / cov.GridRows);
                int row = (int)Math.Round((raster.North - lat) / (raster.North - raster.South) * (raster.Rows - 1));
                if (row > 0 && row < raster.Rows - 1)
                {
                    cellRows.Add(row);
                }
            }
        }

        var tiles = new List<TerrainMesh3D>(plan.Count);
        foreach (PerTileLodDecision d in plan)
        {
            int step = Math.Max(1, d.SubsampleStep);
            int colStartT = d.ColStart;
            int rowStartT = d.RowStart;
            // Read ONE extra cell past the owned crop (the shared boundary with the next tile), clamped at the
            // raster edge, so adjacent tiles meet on the same absolute grid — the crack fix.
            int colEndT = Math.Min(d.ColStart + d.Columns, raster.Columns - 1);
            int rowEndT = Math.Min(d.RowStart + d.Rows, raster.Rows - 1);

            // Welding ratio per edge = coarser-neighbour-step / own-step (1 when same/finer/absent). Powers of two
            // so this divides cleanly. Applied only on the plan tile's OUTER edges (internal sub-seams are same-step).
            int Ratio(int neighbourStep) => neighbourStep > step ? neighbourStep / step : 1;

            // Sub-split a big plan tile so each block stays under the 16-bit limit. Cuts are step-aligned and
            // shared, so internal sub-seams sample identical absolute cells (coincide, no crack); only blocks on
            // the plan tile's true edge carry the neighbour weld ratio.
            int seg = maxTileSide * step;
            int[] colCuts = CutsWithCellBoundaries(colStartT, colEndT, seg, cellCols);
            int[] rowCuts = CutsWithCellBoundaries(rowStartT, rowEndT, seg, cellRows);
            for (int ri = 0; ri < rowCuts.Length - 1; ri++)
            {
                int sr0 = rowCuts[ri];
                int sr1 = rowCuts[ri + 1];
                int rN = sr0 == rowStartT ? Ratio(d.EdgeStepNorth) : 1;
                int rS = sr1 == rowEndT ? Ratio(d.EdgeStepSouth) : 1;
                for (int ci = 0; ci < colCuts.Length - 1; ci++)
                {
                    int sc0 = colCuts[ci];
                    int sc1 = colCuts[ci + 1];
                    int rW = sc0 == colStartT ? Ratio(d.EdgeStepWest) : 1;
                    int rE = sc1 == colEndT ? Ratio(d.EdgeStepEast) : 1;

                    // Capture the loop locals so the build closure (cache miss) reads THIS block's bounds.
                    int bc0 = sc0, bc1 = sc1, br0 = sr0, br1 = sr1, bN = rN, bS = rS, bW = rW, bE = rE;
                    TerrainMesh3D BuildThisBlock() => BuildBlock(
                        raster, options, frame, bc0, bc1, br0, br1, anchor, anchorOffset,
                        default, edgeHeightSource, edgeMatchRows, orthoCoverage, orthoTileIndexOffset, bN, bS, bW, bE, step);

                    if (cache is null)
                    {
                        tiles.Add(BuildThisBlock());
                    }
                    else
                    {
                        // ABSOLUTE block identity (the window is z16-tile-aligned) ⇒ the same ground keeps the same
                        // key across reloads ⇒ the cache hands back the SAME mesh object ⇒ the renderer (SyncTiles,
                        // keyed by object) skips its GPU upload AND we skip re-meshing. Everything that changes the
                        // geometry is in the key, so a hit is byte-identical.
                        var key = new DetailBlockKey(
                            absColOrigin + bc0, absRowOrigin + br0, absColOrigin + bc1, absRowOrigin + br1,
                            step, bN, bS, bW, bE, (int)Math.Round(options.VerticalExaggeration * 1000f));
                        tiles.Add(cache.GetOrBuild(key, BuildThisBlock));
                    }
                }
            }
        }

        return tiles;
    }

    /// <summary>
    /// Inclusive cut points over [start, end] that (a) include every ortho cell <paramref name="boundaries"/>
    /// strictly inside the range, so no sub-block straddles a cell (which would clamp its far-side UV into
    /// stretched edge texels = "strata" stripes), and (b) split each gap between anchors into EQUAL parts no
    /// more than <paramref name="seg"/> cells apart (equal parts avoid a 1–2-column sliver next to a boundary —
    /// the dashed dark line along a cell edge). Consecutive entries are a sub-block's shared range. With no
    /// boundaries it degrades to plain ≤seg even cuts of the whole range.
    /// </summary>
    private static int[] CutsWithCellBoundaries(int start, int end, int seg, SortedSet<int> boundaries)
    {
        var anchors = new SortedSet<int> { start, end };
        foreach (int b in boundaries)
        {
            if (b > start && b < end)
            {
                anchors.Add(b);
            }
        }

        var cuts = new SortedSet<int>();
        var sorted = new List<int>(anchors);
        for (int i = 0; i < sorted.Count - 1; i++)
        {
            int a = sorted[i];
            int b = sorted[i + 1];
            int span = b - a;
            int parts = Math.Max(1, (int)Math.Ceiling((double)span / seg));
            for (int k = 0; k <= parts; k++)
            {
                cuts.Add(a + (int)Math.Round((double)span * k / parts));
            }
        }

        return cuts.ToArray();
    }

    private static TerrainMesh3D BuildBlock(
        DemRaster raster,
        TerrainMeshOptions options,
        MeshFrame frame,
        int colStart,
        int colEnd,
        int rowStart,
        int rowEnd,
        GeoPoint projectionAnchor,
        Vector3 anchorOffset,
        OrthoCell orthoCell = default,
        DemRaster? edgeHeightSource = null,
        int edgeMatchRows = 1,
        OrthoCoverage? orthoCoverage = null,
        int orthoTileIndexOffset = 0,
        int edgeRatioNorth = 1,
        int edgeRatioSouth = 1,
        int edgeRatioWest = 1,
        int edgeRatioEast = 1,
        int step = 1,
        float[]? detailGrid = null,
        MapBounds? emitBounds = null)
    {
        int cols = raster.Columns;
        int rows = raster.Rows;
        int morphBand = Math.Max(1, edgeMatchRows);

        // Sampled grid: vertices at every `step`-th cell over [colStart..colEnd] INCLUSIVE of colEnd (so adjacent
        // strided blocks SHARE their boundary on the absolute grid → no gap). step 1 = every cell (base/legacy,
        // unchanged). Positions below use the ABSOLUTE cell index, so a coarse tile lands on the same world grid
        // as a finer neighbour.
        int[] sCols = SampleAxis(colStart, colEnd, step);
        int[] sRows = SampleAxis(rowStart, rowEnd, step);
        int tileCols = sCols.Length;
        int tileRows = sRows.Length;
        int vertexCount = tileCols * tileRows;
        float exaggeration = options.VerticalExaggeration;

        // The skirt ring size is KNOWN before renting: rent the buffers at their FINAL size so the skirt path
        // never has to Array.Resize them. The old flow rented `vertexCount`, then Resize'd all six arrays to
        // `vertexCount + skirtCount` — which orphaned every rented array (garbage) and returned post-resize
        // sizes into pool buckets that Rent (exact-length) never asked for again: the pool degenerated into
        // pure retention and every flight build paid ~12.6 MB of fresh allocations (measured 2026-07-16 — the
        // "8 GB heap in flight" storm). Sizing up front closes the rent/return cycle for real.
        int skirtCount = options.SkirtDepthMeters > 0f ? (2 * (tileCols + tileRows)) - 4 : 0;
        int finalCount = vertexCount + skirtCount;

        // Rent the large vertex buffers from the pool — a streaming reload rebuilds ~170 tiles, each ~2.5 MB; pooling
        // reuses the arrays instead of churning the Large Object Heap (the cause of the ~1.5 s GC stalls). The renderer
        // returns the upload-only ones after the GL upload (see ReturnBuffersToPool for the ownership contract).
        Vector3[] vertices = MeshBufferPool.Shared.RentVector3(finalCount);
        Vector3[] normals = MeshBufferPool.Shared.RentVector3(finalCount);
        uint[] colors = MeshBufferPool.Shared.RentUInt32(finalCount);
        uint[] baseColors = MeshBufferPool.Shared.RentUInt32(finalCount);
        float[] texCoords = MeshBufferPool.Shared.RentSingle(finalCount * 2);
        // Per-vertex mid-frequency detail amplitude (metres RMS) — the sub-cell relief discarded by a coarsening
        // downsample, sampled from the tile's detail grid (null on the finest/live path ⇒ all 0). The detail grid
        // is row-major cols×rows, identical indexing to the height raster, so a vertex at absolute cell (c,r)
        // reads detailGrid[r*cols + c].
        float[] detail = MeshBufferPool.Shared.RentSingle(finalCount);
        // Index capacity includes the skirt's 6 indices per ring segment — an undersized List doubled its
        // 1.56 MB backing on the last few skirt indices (3.12 MB of garbage per build, measured).
        var indexList = new List<uint>(((tileCols - 1) * (tileRows - 1) * 6) + (skirtCount * 6));
        float noDataValue = raster.NoDataValue;

        // UV maps each vertex to [0,1] within its ORTHO CELL (the texture this tile samples). The default
        // full-extent cell spans the whole raster (legacy global mapping); a finer grid gives local UV so
        // each cell has its own texture.
        OrthoCell cell = orthoCell.Spans ? orthoCell : OrthoCell.Full(cols, rows);
        float uDenom = cell.ColEnd > cell.ColStart ? cell.ColEnd - cell.ColStart : 1;
        float vDenom = cell.RowEnd > cell.RowStart ? cell.RowEnd - cell.RowStart : 1;

        // Geo-referenced ortho (ortho-on-LOD): resolve THIS block's ortho cell from its centre's geographic
        // position in the coverage (MVP: one cell per block). texCoords below then map each vertex's geo into
        // that cell. geoTileIndex overrides the grid cell index for the returned mesh.
        int geoCol = 0, geoRow = 0;
        int geoTileIndex = cell.TileIndex;
        if (orthoCoverage is { } coverage)
        {
            double centreCol = (colStart + colEnd) * 0.5;
            double centreRow = (rowStart + rowEnd) * 0.5;
            double centreLon = cols > 1 ? raster.West + (centreCol / (cols - 1) * (raster.East - raster.West)) : raster.West;
            double centreLat = rows > 1 ? raster.North - (centreRow / (rows - 1) * (raster.North - raster.South)) : raster.North;
            var centre = new GeoPoint(centreLat, centreLon);
            if (coverage.Covers(centre))
            {
                (geoCol, geoRow, geoTileIndex) = coverage.CellAt(centre);
            }
            else
            {
                // Tile centre is OUTSIDE the ortho coverage — sampling CellAt/LocalUv here would clamp to the
                // edge texels and stretch them along the slope (the seam "strata" stripes). -1 makes the renderer
                // fall back to the per-vertex hypsometric colour for this tile instead.
                geoTileIndex = -1;
            }
        }

        // Shift the ortho cell index so this mesh's cells sit AFTER another set in the renderer's flat
        // orthoTiles list (e.g. a high-zoom near-field patch appended past the base scene's cells). An
        // out-of-coverage tile (-1) stays out — no ortho, hypsometric.
        if (geoTileIndex >= 0)
        {
            geoTileIndex += orthoTileIndexOffset;
        }

        // Vertex positions in the full-raster world frame. Row 0 = north edge = +Y; last row = -Y.
        for (int localRow = 0; localRow < tileRows; localRow++)
        {
            int r = sRows[localRow];
            double yMeters = frame.HalfHeightMeters - (frame.CellHeightMeters * r);
            for (int localCol = 0; localCol < tileCols; localCol++)
            {
                int c = sCols[localCol];
                double xMeters = -frame.HalfWidthMeters + (frame.CellWidthMeters * c);
                float z = raster[c, r] * exaggeration;

                // Edge matching (Krok 4c): morph the outer band toward the coarse base so a detail patch melts
                // into it over `morphBand` rows instead of stepping — weight 1 at the very edge → 0 one band
                // in. A no-data base sample leaves the detail height.
                if (edgeHeightSource is not null)
                {
                    int distFromEdge = Math.Min(Math.Min(c, cols - 1 - c), Math.Min(r, rows - 1 - r));
                    if (distFromEdge < morphBand)
                    {
                        double lon = cols > 1 ? raster.West + ((double)c / (cols - 1) * (raster.East - raster.West)) : raster.West;
                        double lat = rows > 1 ? raster.North - ((double)r / (rows - 1) * (raster.North - raster.South)) : raster.North;
                        double baseElev = edgeHeightSource.SampleBilinear(lon, lat);
                        if (baseElev != edgeHeightSource.NoDataValue)
                        {
                            float baseZ = (float)(baseElev * exaggeration);
                            float weight = (float)(morphBand - distFromEdge) / morphBand;
                            z = (baseZ * weight) + (z * (1f - weight));
                        }
                    }
                }

                vertices[(localRow * tileCols) + localCol] =
                    new Vector3((float)xMeters + anchorOffset.X, (float)yMeters + anchorOffset.Y, z);
            }
        }

        // Per-vertex normals via central differences in elevation, sampling the full raster so tile-edge
        // normals use real neighbour cells (continuous shading across seams) rather than clamped values.
        // The radius widens the difference baseline to low-pass the NORMAL field (softer facets) without
        // altering any elevation — only the lighting normals are smoothed, never the heights.
        int normalRadius = Math.Max(1, options.NormalSmoothingRadius);
        for (int localRow = 0; localRow < tileRows; localRow++)
        {
            int r = sRows[localRow];
            int rN = Math.Max(r - normalRadius, 0);
            int rS = Math.Min(r + normalRadius, rows - 1);
            for (int localCol = 0; localCol < tileCols; localCol++)
            {
                int c = sCols[localCol];
                int cW = Math.Max(c - normalRadius, 0);
                int cE = Math.Min(c + normalRadius, cols - 1);

                float zE = raster[cE, r] * exaggeration;
                float zW = raster[cW, r] * exaggeration;
                float zN = raster[c, rN] * exaggeration;
                float zS = raster[c, rS] * exaggeration;

                float dx = (float)((cE - cW) * frame.CellWidthMeters);
                float dy = (float)((rS - rN) * frame.CellHeightMeters);
                float dzdx = dx > 0f ? (zE - zW) / dx : 0f;
                // Row index grows southward, so dz/dy (north-positive) flips sign.
                float dzdy = dy > 0f ? (zN - zS) / dy : 0f;

                Vector3 normal = Vector3.Normalize(new Vector3(-dzdx, -dzdy, 1f));
                int li = (localRow * tileCols) + localCol;
                normals[li] = normal;
                // No pre-baked detail grid (the ring-LOD base / live per-tile paths, unlike the baked pyramid,
                // mesh straight off the native raster): at step>1 this vertex is a single POINT sample, so
                // everything in its step-sized window is real relief the mesh never shows — recover it ON THE
                // FLY from the same raster this loop already reads, faded by step (StepDetailFadeHalfLife) so
                // far/coarse ground doesn't out-bump near/fine ground. Step 1 (native resolution, incl. every
                // baked z16 tile — BakedTileMeshBuilder always calls Build/BuildTiles at step 1) discards
                // NOTHING, but the 1 m LiDAR itself cannot resolve sub-metre rock/scree microtexture — that is
                // below the sensor's own resolution, no fix on this data could ever recover it. NativeMicroDetail
                // adds a modest, capped synthetic bump extrapolated from the ground's own small-scale roughness
                // (near-zero on genuinely smooth ground — a lake, graded scree/snow — non-zero elsewhere).
                detail[li] = detailGrid is not null
                    ? detailGrid[(r * cols) + c]
                    : step > 1
                        ? StepResidualRms(raster, c, r, step, cols, rows) * DistanceFade(step, StepDetailFadeHalfLife)
                        : NativeMicroDetail(raster, c, r, cols, rows);
                if (orthoCoverage is { } cov)
                {
                    double vlon = cols > 1 ? raster.West + ((double)c / (cols - 1) * (raster.East - raster.West)) : raster.West;
                    double vlat = rows > 1 ? raster.North - ((double)r / (rows - 1) * (raster.North - raster.South)) : raster.North;
                    (float gu, float gv) = cov.LocalUv(new GeoPoint(vlat, vlon), geoCol, geoRow);
                    texCoords[li * 2] = gu;
                    texCoords[(li * 2) + 1] = gv;
                }
                else
                {
                    texCoords[li * 2] = (c - cell.ColStart) / uDenom;
                    texCoords[(li * 2) + 1] = (r - cell.RowStart) / vDenom;
                }

                uint baseColor = HypsometricColor(raster[c, r]);
                if (options.OverlayTintArgb is { } tint)
                {
                    baseColor = BlendColor(baseColor, tint, options.OverlayTintStrength);
                }

                // Curvature AO in the (otherwise always-0xFF) ALPHA byte: gully/bowl floors darken, ridges
                // stay open. Rides the existing colour attribute through EVERY mesh path (base, baked z16,
                // legacy) with no new plumbing; the terrain shader multiplies its light sum by vColor.a.
                float ao = TerrainCurvatureAo.At(raster, c, r, frame.CellWidthMeters);
                baseColors[li] = (baseColor & 0x00FFFFFFu) | ((uint)(byte)(ao * 255f) << 24);
                float lambert = Math.Max(0f, Vector3.Dot(normal, options.LightDirection));
                float shade = options.AmbientFactor + ((1f - options.AmbientFactor) * lambert);
                colors[li] = ApplyShade(baseColor, shade);
            }
        }

        // Two triangles per grid cell (clockwise as seen from +Z), indices local to the tile. A triangle
        // whose corner sits on a no-data cell is dropped (Krok 4c), leaving a hole so the coarse base shows
        // through instead of flat geometry spiking over the gap (the yellow "blinds" / flat green rectangle).
        for (int lr = 0; lr < tileRows - 1; lr++)
        {
            for (int lc = 0; lc < tileCols - 1; lc++)
            {
                int gcW = sCols[lc];
                int gcE = sCols[lc + 1];
                int grN = sRows[lr];
                int grS = sRows[lr + 1];
                bool nw = raster[gcW, grN] == noDataValue;
                bool ne = raster[gcE, grN] == noDataValue;
                bool sw = raster[gcW, grS] == noDataValue;
                bool se = raster[gcE, grS] == noDataValue;

                // Weld in-between edge vertices to the coarser neighbour's anchors (ratio > 1) so a different-step
                // seam meets 1:1 (no T-junction crack). A triangle that collapses after welding is dropped.
                uint i00 = WeldEdgeVertex(lr, lc, tileCols, tileRows, edgeRatioNorth, edgeRatioSouth, edgeRatioWest, edgeRatioEast);
                uint i10 = WeldEdgeVertex(lr, lc + 1, tileCols, tileRows, edgeRatioNorth, edgeRatioSouth, edgeRatioWest, edgeRatioEast);
                uint i01 = WeldEdgeVertex(lr + 1, lc, tileCols, tileRows, edgeRatioNorth, edgeRatioSouth, edgeRatioWest, edgeRatioEast);
                uint i11 = WeldEdgeVertex(lr + 1, lc + 1, tileCols, tileRows, edgeRatioNorth, edgeRatioSouth, edgeRatioWest, edgeRatioEast);

                // Triangle 1: NW, NE, SW
                if (!nw && !ne && !sw && i00 != i10 && i10 != i01 && i00 != i01)
                {
                    indexList.Add(i00);
                    indexList.Add(i10);
                    indexList.Add(i01);
                }

                // Triangle 2: NE, SE, SW
                if (!ne && !se && !sw && i10 != i11 && i11 != i01 && i10 != i01)
                {
                    indexList.Add(i10);
                    indexList.Add(i11);
                    indexList.Add(i01);
                }
            }
        }

        // Skirt (Model 1): hang a vertical apron from the tile perimeter so the crack to a different-resolution
        // neighbour is filled with real terrain-coloured geometry, not a see-through gap. Appends a lowered
        // copy of the boundary ring + wall triangles. 32-bit indices, so the skirt ring can push the vertex
        // count past 65 536 (e.g. a 256² baked tile + skirt) without overflowing.
        if (options.SkirtDepthMeters > 0f)
        {
            float drop = options.SkirtDepthMeters * exaggeration;
            var ring = new List<int>(2 * (tileCols + tileRows));
            for (int lc = 0; lc < tileCols; lc++) { ring.Add(lc); }                                  // top edge, W→E
            for (int lr = 1; lr < tileRows; lr++) { ring.Add((lr * tileCols) + tileCols - 1); }       // right edge, N→S
            for (int lc = tileCols - 2; lc >= 0; lc--) { ring.Add(((tileRows - 1) * tileCols) + lc); } // bottom edge, E→W
            for (int lr = tileRows - 2; lr >= 1; lr--) { ring.Add(lr * tileCols); }                   // left edge, S→N

            // The buffers were rented at vertexCount + skirtCount up front (see the rent block) — resizing
            // rented arrays here is exactly what used to orphan them and disarm the pool.
            int skirtBase = vertexCount;
            System.Diagnostics.Debug.Assert(
                ring.Count == skirtCount, "skirt ring size must match the pre-rented allowance");

            for (int j = 0; j < skirtCount; j++)
            {
                int src = ring[j];
                int dst = skirtBase + j;
                Vector3 v = vertices[src];
                vertices[dst] = new Vector3(v.X, v.Y, v.Z - drop);
                normals[dst] = normals[src];
                colors[dst] = colors[src];
                baseColors[dst] = baseColors[src];
                texCoords[dst * 2] = texCoords[src * 2];
                texCoords[(dst * 2) + 1] = texCoords[(src * 2) + 1];
                detail[dst] = detail[src]; // skirts are hidden seams — copy the ring vertex's detail
            }

            for (int j = 0; j < skirtCount; j++)
            {
                int jn = (j + 1) % skirtCount;
                uint topA = (uint)ring[j];
                uint topB = (uint)ring[jn];
                uint botA = (uint)(skirtBase + j);
                uint botB = (uint)(skirtBase + jn);
                indexList.Add(topA);
                indexList.Add(topB);
                indexList.Add(botB);
                indexList.Add(topA);
                indexList.Add(botB);
                indexList.Add(botA);
            }
        }

        uint[] indices = indexList.ToArray();

        return new TerrainMesh3D(
            vertices,
            normals,
            colors,
            baseColors,
            texCoords,
            detail,
            indices,
            anchorOffset,
            frame.HorizontalExtent,
            exaggeration,
            emitBounds ?? raster.Bounds,
            options.LightDirection,
            options.AmbientFactor,
            geoTileIndex,
            projectionAnchor);
    }

    // A window wider than this (per axis) is strided down to ~this many samples instead of scanned exhaustively.
    // Near/mid-field steps (≤~12) stay exhaustive — that is exactly the visually-resolvable range. Far-field ring-
    // LOD base tiles can carry step 32-64+ (a whole-Tatra-visible tile); scanning every one of those step² cells
    // per vertex measured as multi-SECOND frame stalls (10.7 s observed at step≈64) — a strided subset keeps the
    // cost O(1) per vertex regardless of step, while every sampled value is still a real raster cell (not
    // fabricated), so the residual stays a genuine, if sparser, measurement of local relief.
    private const int MaxResidualSamplesPerAxis = 7;

    // step≈2 (barely coarsened, near-camera) keeps ~94% strength; step≈32 (a far ring-LOD-base tile) fades to
    // ~34%; step≈64 to ~20%. Tuned so the on-the-fly path's existing near-field behaviour is essentially
    // unchanged while distant, heavily-decimated ground tapers off instead of being shown at full bump strength.
    private const float StepDetailFadeHalfLife = 16f;

    /// <summary>
    /// Distance/coarseness fade in (0, 1], applied to a detail amplitude before it reaches the shader: 1 at
    /// <paramref name="coarseness"/> 1 (native resolution), decaying toward 0 as <paramref name="coarseness"/>
    /// grows. The LOD selector only ever gives a large step (or a coarse baked zoom) to ground FAR from the
    /// camera, so without this a distant, heavily-decimated tile's naturally larger window/box-average residual
    /// would show MORE synthetic bump than nearby fine terrain — backwards from what the eye expects. Shaped as
    /// 1 / (1 + (coarseness-1)/halfLife) rather than a hard cutoff so LOD-tier changes fade smoothly (no pop).
    /// Internal (not private) so <see cref="BakedTileMeshBuilder"/> can apply the SAME shape to its own
    /// zoom-derived coarseness before handing a tile's DetailRms in as this class's detailGrid.
    /// </summary>
    internal static float DistanceFade(float coarseness, float halfLife)
        => 1f / (1f + ((coarseness - 1f) / halfLife));

    /// <summary>
    /// RMS (metres) of a step-sampled vertex's discarded neighbourhood about its own mean — the same "fix B"
    /// residual maths as <see cref="BakedDemDownsampler"/>, computed ON THE FLY from the native raster instead
    /// of pre-baked, because a step&gt;1 tile here is POINT-sampled (one raw cell per vertex), not box-averaged:
    /// the whole window around (<paramref name="c"/>, <paramref name="r"/>) is real relief the mesh never shows.
    /// NoData cells are excluded; fewer than 2 valid cells (a NoData-heavy edge) returns 0 rather than fabricate
    /// a value from a single sample.
    /// </summary>
    private static float StepResidualRms(DemRaster raster, int c, int r, int step, int cols, int rows)
    {
        int half = step / 2;
        int colFrom = Math.Max(0, c - half);
        int colTo = Math.Min(cols - 1, c + half);
        int rowFrom = Math.Max(0, r - half);
        int rowTo = Math.Min(rows - 1, r + half);
        float noData = raster.NoDataValue;

        int colStride = Math.Max(1, (colTo - colFrom + 1) / MaxResidualSamplesPerAxis);
        int rowStride = Math.Max(1, (rowTo - rowFrom + 1) / MaxResidualSamplesPerAxis);

        double sum = 0.0;
        double sumSq = 0.0;
        int valid = 0;
        for (int rr = rowFrom; rr <= rowTo; rr += rowStride)
        {
            for (int cc = colFrom; cc <= colTo; cc += colStride)
            {
                float v = raster[cc, rr];
                if (v.Equals(noData) || float.IsNaN(v))
                {
                    continue;
                }

                sum += v;
                sumSq += (double)v * v;
                valid++;
            }
        }

        if (valid < 2)
        {
            return 0f;
        }

        double mean = sum / valid;
        double variance = Math.Max(0.0, (sumSq / valid) - (mean * mean));
        return (float)Math.Sqrt(variance);
    }

    // ±2 native cells (~5-8 m at 1-1.6 m/px) — a small, local window; NativeDetailGain/Cap deliberately keep the
    // result FAR below what the same window's raw RMS would read as real relief (see StepResidualRms) — this is
    // extrapolation below the source data's own resolution, not a measurement, so it must never be mistaken for
    // (or overpower) genuine large-scale shape. Internal (not private) so a neighbour-halo builder can size the
    // halo to cover this window's reach (step/2 cells) — an undersized halo re-clamps the window at tile borders.
    internal const int NativeDetailWindowStep = 4;
    private const float NativeDetailGain = 0.75f;
    private const float NativeDetailCap = 0.9f;

    /// <summary>
    /// Modest, capped synthetic micro-bump amplitude (metres) for a NATIVE-resolution vertex (step 1 — every
    /// baked z16 tile, and any ring-LOD-base/live-detail vertex close enough to sample every raster cell). A
    /// native vertex discards nothing (<see cref="StepResidualRms"/> is for step&gt;1 tiles that skip cells), but
    /// 1 m LiDAR itself cannot resolve sub-metre rock/scree microtexture — that is below the SENSOR's own
    /// resolution, so no downsample/re-bake fix on this data could ever recover it. This extrapolates a plausible
    /// amount from the ground's own small-scale roughness (<see cref="StepResidualRms"/> over a small fixed
    /// window): genuinely smooth ground (a lake, a graded scree/snow floor) has near-zero local variation and
    /// gets near-zero synthetic detail; locally-varied ground gets a capped FRACTION of that variation, never the
    /// full measured amplitude — it is a guess dressed as texture, not a fact, and must read as modest.
    /// </summary>
    private static float NativeMicroDetail(DemRaster raster, int c, int r, int cols, int rows)
        => Math.Min(StepResidualRms(raster, c, r, NativeDetailWindowStep, cols, rows) * NativeDetailGain, NativeDetailCap);

    /// <summary>
    /// Hypsometric (elevation-based) base colour, opaque ARGB.
    /// Ramp: deep green (≤500 m) → olive → light brown → grey → white (≥2500 m).
    /// </summary>
    public static uint HypsometricColor(double elevationMeters)
    {
        // Anchor stops: (elevation, R, G, B).
        Span<(double Elev, byte R, byte G, byte B)> stops = stackalloc (double, byte, byte, byte)[]
        {
            (300.0, 56, 110, 56),    // dark forest green
            (800.0, 90, 140, 60),    // alpine green
            (1300.0, 150, 145, 80),  // olive
            (1700.0, 175, 140, 100), // brown
            (2100.0, 180, 175, 170), // grey
            (2600.0, 245, 245, 245), // near-white
        };

        if (elevationMeters <= stops[0].Elev)
        {
            return ToArgb(stops[0].R, stops[0].G, stops[0].B);
        }

        for (int i = 0; i < stops.Length - 1; i++)
        {
            if (elevationMeters <= stops[i + 1].Elev)
            {
                double t = (elevationMeters - stops[i].Elev) / (stops[i + 1].Elev - stops[i].Elev);
                byte r = LerpByte(stops[i].R, stops[i + 1].R, t);
                byte g = LerpByte(stops[i].G, stops[i + 1].G, t);
                byte b = LerpByte(stops[i].B, stops[i + 1].B, t);
                return ToArgb(r, g, b);
            }
        }

        var top = stops[^1];
        return ToArgb(top.R, top.G, top.B);
    }

    private static byte LerpByte(byte a, byte b, double t)
    {
        double v = a + ((b - a) * t);
        return (byte)Math.Clamp(v, 0.0, 255.0);
    }

    private static uint ToArgb(byte r, byte g, byte b)
    {
        return (uint)((0xFF << 24) | (r << 16) | (g << 8) | b);
    }

    private static uint ApplyShade(uint argb, float shade)
    {
        shade = Math.Clamp(shade, 0f, 1f);
        byte a = (byte)((argb >> 24) & 0xFF);
        byte r = (byte)((argb >> 16) & 0xFF);
        byte g = (byte)((argb >> 8) & 0xFF);
        byte b = (byte)(argb & 0xFF);
        r = (byte)(r * shade);
        g = (byte)(g * shade);
        b = (byte)(b * shade);
        return (uint)((a << 24) | (r << 16) | (g << 8) | b);
    }

    // Linear per-channel blend of two opaque ARGB colours: result = a·(1−t) + b·t. Used to tint LOD detail
    // tiles so they read distinctly from the base.
    private static uint BlendColor(uint a, uint b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        byte ar = (byte)((a >> 16) & 0xFF), ag = (byte)((a >> 8) & 0xFF), ab = (byte)(a & 0xFF);
        byte br = (byte)((b >> 16) & 0xFF), bg = (byte)((b >> 8) & 0xFF), bb = (byte)(b & 0xFF);
        byte r = (byte)(ar + ((br - ar) * t));
        byte g = (byte)(ag + ((bg - ag) * t));
        byte bl = (byte)(ab + ((bb - ab) * t));
        return 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | bl;
    }
}