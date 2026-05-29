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

    /// <summary>Per-vertex ARGB colours (alpha in high byte).</summary>
    public uint[] Colors { get; }

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

    private TerrainMesh3D(
        Vector3[] vertices,
        Vector3[] normals,
        uint[] colors,
        ushort[] indices,
        Vector3 center,
        float horizontalExtent,
        float verticalExaggeration,
        MapBounds bounds)
    {
        Vertices = vertices;
        Normals = normals;
        Colors = colors;
        Indices = indices;
        Center = center;
        HorizontalExtent = horizontalExtent;
        VerticalExaggeration = verticalExaggeration;
        Bounds = bounds;
    }

    /// <summary>
    /// Projects a geographic point + raw elevation into the mesh's world-space coordinate system
    /// (X east, Y north, Z up). Elevation is scaled by <see cref="VerticalExaggeration"/> so overlay
    /// geometry (trails, routes) sits at the same vertical scale as the rendered terrain.
    /// </summary>
    public Vector3 GeoToWorld(GeoPoint geoPoint, float elevationMeters)
    {
        double centerLat = (Bounds.NorthEast.Latitude + Bounds.SouthWest.Latitude) / 2.0;
        double centerLon = (Bounds.NorthEast.Longitude + Bounds.SouthWest.Longitude) / 2.0;
        double metersPerLonDegree = MetersPerLatDegree * Math.Cos(centerLat * Math.PI / 180.0);
        double xMeters = (geoPoint.Longitude - centerLon) * metersPerLonDegree;
        double yMeters = (geoPoint.Latitude - centerLat) * MetersPerLatDegree;
        return new Vector3((float)xMeters, (float)yMeters, elevationMeters * VerticalExaggeration);
    }

    /// <summary>
    /// Inverse of <see cref="GeoToWorld"/>: maps a world-space point (X east, Y north, in metres)
    /// back to its geographic position. The Z/elevation component is ignored — only the planar
    /// XY position determines longitude/latitude. Used to translate a 3D camera focus point into a
    /// 2D map centre so switching between 3D and 2D keeps the same place framed.
    /// </summary>
    public GeoPoint WorldToGeo(Vector3 worldPoint)
    {
        double centerLat = (Bounds.NorthEast.Latitude + Bounds.SouthWest.Latitude) / 2.0;
        double centerLon = (Bounds.NorthEast.Longitude + Bounds.SouthWest.Longitude) / 2.0;
        double metersPerLonDegree = MetersPerLatDegree * Math.Cos(centerLat * Math.PI / 180.0);
        double longitude = centerLon + (worldPoint.X / metersPerLonDegree);
        double latitude = centerLat + (worldPoint.Y / MetersPerLatDegree);
        return new GeoPoint(latitude, longitude);
    }

    /// <summary>Largest vertex count addressable by 16-bit (ushort) triangle indices.</summary>
    private const int MaxVerticesPerMesh = ushort.MaxValue + 1;

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

        MeshFrame frame = ComputeFrame(raster);
        return BuildBlock(raster, options, frame, 0, raster.Columns - 1, 0, raster.Rows - 1);
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
    public static IReadOnlyList<TerrainMesh3D> BuildTiles(DemRaster raster, TerrainMeshOptions? options = null, int maxTileSide = 255)
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

        options ??= new TerrainMeshOptions();
        MeshFrame frame = ComputeFrame(raster);
        int cols = raster.Columns;
        int rows = raster.Rows;

        var tiles = new List<TerrainMesh3D>();
        // Step by maxTileSide and make ranges inclusive, so tile N's last row/column equals tile N+1's
        // first — the seam vertices are bit-identical in both tiles, leaving no gap between them.
        for (int r0 = 0; r0 < rows - 1; r0 += maxTileSide)
        {
            int r1 = Math.Min(r0 + maxTileSide, rows - 1);
            for (int c0 = 0; c0 < cols - 1; c0 += maxTileSide)
            {
                int c1 = Math.Min(c0 + maxTileSide, cols - 1);
                tiles.Add(BuildBlock(raster, options, frame, c0, c1, r0, r1));
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

    private static MeshFrame ComputeFrame(DemRaster raster)
    {
        int cols = raster.Columns;
        int rows = raster.Rows;
        double centerLat = (raster.North + raster.South) / 2.0;
        double metersPerLonDegree = MetersPerLatDegree * Math.Cos(centerLat * Math.PI / 180.0);
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
        int rowEnd)
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
        var indices = new ushort[(tileCols - 1) * (tileRows - 1) * 2 * 3];

        // Vertex positions in the full-raster world frame. Row 0 = north edge = +Y; last row = -Y.
        for (int r = rowStart; r <= rowEnd; r++)
        {
            double yMeters = frame.HalfHeightMeters - (frame.CellHeightMeters * r);
            int localRow = r - rowStart;
            for (int c = colStart; c <= colEnd; c++)
            {
                double xMeters = -frame.HalfWidthMeters + (frame.CellWidthMeters * c);
                float z = raster[c, r] * exaggeration;
                vertices[(localRow * tileCols) + (c - colStart)] = new Vector3((float)xMeters, (float)yMeters, z);
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

                uint baseColor = HypsometricColor(raster[c, r]);
                float lambert = Math.Max(0f, Vector3.Dot(normal, options.LightDirection));
                float shade = options.AmbientFactor + ((1f - options.AmbientFactor) * lambert);
                colors[li] = ApplyShade(baseColor, shade);
            }
        }

        // Two triangles per grid cell (clockwise as seen from +Z), indices local to the tile.
        int idx = 0;
        for (int lr = 0; lr < tileRows - 1; lr++)
        {
            for (int lc = 0; lc < tileCols - 1; lc++)
            {
                ushort i00 = (ushort)((lr * tileCols) + lc);
                ushort i10 = (ushort)((lr * tileCols) + lc + 1);
                ushort i01 = (ushort)(((lr + 1) * tileCols) + lc);
                ushort i11 = (ushort)(((lr + 1) * tileCols) + lc + 1);

                // Triangle 1: NW, NE, SW
                indices[idx++] = i00;
                indices[idx++] = i10;
                indices[idx++] = i01;
                // Triangle 2: NE, SE, SW
                indices[idx++] = i10;
                indices[idx++] = i11;
                indices[idx++] = i01;
            }
        }

        return new TerrainMesh3D(
            vertices,
            normals,
            colors,
            indices,
            Vector3.Zero,
            frame.HorizontalExtent,
            exaggeration,
            raster.Bounds);
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
}