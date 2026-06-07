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
    private const double MetersPerLatDegree = 111_320.0;

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

    /// <summary>Index (row-major) of the ortho texture cell this mesh tile samples; 0 for a single full-extent ortho.</summary>
    public int OrthoTileIndex { get; }

    /// <summary>Triangle index buffer (3 ushorts per triangle).</summary>
    public ushort[] Indices { get; }

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

    private TerrainMesh3D(
        Vector3[] vertices,
        Vector3[] normals,
        uint[] colors,
        uint[] baseColors,
        float[] texCoords,
        ushort[] indices,
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
        OrthoTileIndex = orthoTileIndex;
        Indices = indices;
        Center = center;
        HorizontalExtent = horizontalExtent;
        VerticalExaggeration = verticalExaggeration;
        Bounds = bounds;
        LightDirection = lightDirection;
        AmbientFactor = ambientFactor;

        // Compute Z range over vertices once at construction so the host can ask without iterating
        // every frame. Empty meshes (shouldn't happen — Build guards) report 0..0.
        float minZ = float.PositiveInfinity;
        float maxZ = float.NegativeInfinity;
        for (int i = 0; i < vertices.Length; i++)
        {
            float z = vertices[i].Z;
            if (z < minZ)
            {
                minZ = z;
            }
            if (z > maxZ)
            {
                maxZ = z;
            }
        }
        MinElevationZ = float.IsPositiveInfinity(minZ) ? 0f : minZ;
        MaxElevationZ = float.IsNegativeInfinity(maxZ) ? 0f : maxZ;
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

    /// <summary>Largest vertex count addressable by 16-bit (ushort) triangle indices.</summary>
    private const int MaxVerticesPerMesh = ushort.MaxValue + 1;

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
    /// Returns <paramref name="parts"/>+1 grid-index boundaries splitting [0, count-1] into contiguous
    /// ranges that share their seam index with the next range (so adjacent meshes have no crack).
    /// </summary>
    private static int[] SplitPoints(int count, int parts)
    {
        var splits = new int[parts + 1];
        for (int i = 0; i <= parts; i++)
        {
            splits[i] = (int)Math.Round((double)(count - 1) * i / parts);
        }
        return splits;
    }

    /// <summary>
    /// Builds a single terrain mesh from a DEM raster. The raster must fit within the 16-bit index
    /// limit (≤ 65 536 vertices); larger rasters must use <see cref="BuildTiles"/>.
    /// </summary>
    /// <param name="raster">Source DEM.</param>
    /// <param name="options">Optional tuning; default options use NW sun at 2× vertical exaggeration.</param>
    public static TerrainMesh3D Build(DemRaster raster, TerrainMeshOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(raster);
        options ??= new TerrainMeshOptions();

        // Indices are ushort, so the *largest index value* must fit, i.e. vertexCount ≤ 65536.
        if ((long)raster.Columns * raster.Rows > MaxVerticesPerMesh)
        {
            throw new ArgumentException(
                "DEM raster is too large for 16-bit triangle indices; use BuildTiles for high-resolution rasters.",
                nameof(raster));
        }

        var rasterCentre = new GeoPoint(
            (raster.North + raster.South) / 2.0, (raster.East + raster.West) / 2.0);
        MeshFrame frame = ComputeFrame(raster, rasterCentre.Latitude);
        return BuildBlock(raster, options, frame, 0, raster.Columns - 1, 0, raster.Rows - 1, rasterCentre, Vector3.Zero);
    }

    /// <summary>
    /// Builds a high-resolution terrain as a set of mesh tiles, each within the 16-bit index limit, so a
    /// DEM far larger than 65 536 cells can be rendered at full detail (one <c>SKVertices</c> per tile).
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
    /// <param name="edgeHeightSource">Optional coarser raster (e.g. the LOD base) to pin this raster's OUTER
    /// perimeter vertices to (edge matching, Krok 4c). Null (default) keeps every vertex at its own height.
    /// When set, boundary vertices take the source's bilinear height so a streamed detail patch meets the
    /// base seamlessly instead of stepping away from it; no-data source samples leave the detail height.</param>
    public static IReadOnlyList<TerrainMesh3D> BuildTiles(
        DemRaster raster,
        TerrainMeshOptions? options = null,
        int maxTileSide = 255,
        int orthoGridCols = 1,
        int orthoGridRows = 1,
        GeoPoint? projectionAnchor = null,
        DemRaster? edgeHeightSource = null)
    {
        ArgumentNullException.ThrowIfNull(raster);
        if (maxTileSide < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTileSide), maxTileSide, "maxTileSide must be at least 1.");
        }

        if ((maxTileSide + 1L) * (maxTileSide + 1L) > MaxVerticesPerMesh)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxTileSide), maxTileSide, "A tile of this side would exceed the 16-bit vertex limit.");
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

        var tiles = new List<TerrainMesh3D>();

        // Tile per ortho cell so a mesh tile never straddles a texture boundary: each mesh tile samples one
        // ortho cell with UV local to that cell. Cells share their seam row/column with the neighbour, and
        // mesh sub-tiles use inclusive ranges, so there are no cracks and textures meet seamlessly under
        // ClampToEdge. orthoGrid 1×1 → one cell over the whole raster (the legacy single-texture path).
        int[] colSplits = SplitPoints(cols, orthoGridCols);
        int[] rowSplits = SplitPoints(rows, orthoGridRows);

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
                        tiles.Add(BuildBlock(raster, options, frame, c0, c1, r0, r1, anchor, anchorOffset, cell, edgeHeightSource));
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
        DemRaster? edgeHeightSource = null)
    {
        int cols = raster.Columns;
        int rows = raster.Rows;
        int tileCols = colEnd - colStart + 1;
        int tileRows = rowEnd - rowStart + 1;
        int vertexCount = tileCols * tileRows;
        float exaggeration = options.VerticalExaggeration;

        var vertices = new Vector3[vertexCount];
        var normals = new Vector3[vertexCount];
        var colors = new uint[vertexCount];
        var baseColors = new uint[vertexCount];
        var texCoords = new float[vertexCount * 2];
        var indexList = new List<ushort>((tileCols - 1) * (tileRows - 1) * 6);
        float noDataValue = raster.NoDataValue;

        // UV maps each vertex to [0,1] within its ORTHO CELL (the texture this tile samples). The default
        // full-extent cell spans the whole raster (legacy global mapping); a finer grid gives local UV so
        // each cell has its own texture.
        OrthoCell cell = orthoCell.Spans ? orthoCell : OrthoCell.Full(cols, rows);
        float uDenom = cell.ColEnd > cell.ColStart ? cell.ColEnd - cell.ColStart : 1;
        float vDenom = cell.RowEnd > cell.RowStart ? cell.RowEnd - cell.RowStart : 1;

        // Vertex positions in the full-raster world frame. Row 0 = north edge = +Y; last row = -Y.
        for (int r = rowStart; r <= rowEnd; r++)
        {
            double yMeters = frame.HalfHeightMeters - (frame.CellHeightMeters * r);
            int localRow = r - rowStart;
            for (int c = colStart; c <= colEnd; c++)
            {
                double xMeters = -frame.HalfWidthMeters + (frame.CellWidthMeters * c);
                float z = raster[c, r] * exaggeration;

                // Edge matching (Krok 4c): pin OUTER-perimeter vertices to the coarse base so a detail patch
                // meets it seamlessly instead of stepping. A no-data base sample leaves the detail height.
                if (edgeHeightSource is not null && (c == 0 || c == cols - 1 || r == 0 || r == rows - 1))
                {
                    double lon = cols > 1 ? raster.West + ((double)c / (cols - 1) * (raster.East - raster.West)) : raster.West;
                    double lat = rows > 1 ? raster.North - ((double)r / (rows - 1) * (raster.North - raster.South)) : raster.North;
                    double baseElev = edgeHeightSource.SampleBilinear(lon, lat);
                    if (baseElev != edgeHeightSource.NoDataValue)
                    {
                        z = (float)(baseElev * exaggeration);
                    }
                }

                vertices[(localRow * tileCols) + (c - colStart)] =
                    new Vector3((float)xMeters + anchorOffset.X, (float)yMeters + anchorOffset.Y, z);
            }
        }

        // Per-vertex normals via central differences in elevation, sampling the full raster so tile-edge
        // normals use real neighbour cells (continuous shading across seams) rather than clamped values.
        for (int r = rowStart; r <= rowEnd; r++)
        {
            int rN = Math.Max(r - 1, 0);
            int rS = Math.Min(r + 1, rows - 1);
            int localRow = r - rowStart;
            for (int c = colStart; c <= colEnd; c++)
            {
                int cW = Math.Max(c - 1, 0);
                int cE = Math.Min(c + 1, cols - 1);

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
                int li = (localRow * tileCols) + (c - colStart);
                normals[li] = normal;
                texCoords[li * 2] = (c - cell.ColStart) / uDenom;
                texCoords[(li * 2) + 1] = (r - cell.RowStart) / vDenom;

                uint baseColor = HypsometricColor(raster[c, r]);
                if (options.OverlayTintArgb is { } tint)
                {
                    baseColor = BlendColor(baseColor, tint, options.OverlayTintStrength);
                }
                baseColors[li] = baseColor;
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
                ushort i00 = (ushort)((lr * tileCols) + lc);
                ushort i10 = (ushort)((lr * tileCols) + lc + 1);
                ushort i01 = (ushort)(((lr + 1) * tileCols) + lc);
                ushort i11 = (ushort)(((lr + 1) * tileCols) + lc + 1);

                int gc = colStart + lc;
                int gr = rowStart + lr;
                bool nw = raster[gc, gr] == noDataValue;
                bool ne = raster[gc + 1, gr] == noDataValue;
                bool sw = raster[gc, gr + 1] == noDataValue;
                bool se = raster[gc + 1, gr + 1] == noDataValue;

                // Triangle 1: NW, NE, SW
                if (!nw && !ne && !sw)
                {
                    indexList.Add(i00);
                    indexList.Add(i10);
                    indexList.Add(i01);
                }

                // Triangle 2: NE, SE, SW
                if (!ne && !se && !sw)
                {
                    indexList.Add(i10);
                    indexList.Add(i11);
                    indexList.Add(i01);
                }
            }
        }

        ushort[] indices = indexList.ToArray();

        return new TerrainMesh3D(
            vertices,
            normals,
            colors,
            baseColors,
            texCoords,
            indices,
            anchorOffset,
            frame.HorizontalExtent,
            exaggeration,
            raster.Bounds,
            options.LightDirection,
            options.AmbientFactor,
            cell.TileIndex,
            projectionAnchor);
    }

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