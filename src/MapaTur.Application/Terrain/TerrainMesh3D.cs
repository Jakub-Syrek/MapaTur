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
    /// Builds a terrain mesh from a DEM raster.
    /// </summary>
    /// <param name="raster">Source DEM.</param>
    /// <param name="options">Optional tuning; default options use NW sun at 2× vertical exaggeration.</param>
    public static TerrainMesh3D Build(DemRaster raster, TerrainMeshOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(raster);
        options ??= new TerrainMeshOptions();

        int cols = raster.Columns;
        int rows = raster.Rows;
        int vertexCount = cols * rows;
        // Indices are ushort, so the *largest index value* must fit, i.e. vertexCount ≤ 65536.
        // The index buffer itself is allowed to be longer than that.
        if (vertexCount > ushort.MaxValue + 1)
        {
            throw new ArgumentException(
                "DEM raster is too large for 16-bit triangle indices; subsample before meshing.",
                nameof(raster));
        }

        double centerLat = (raster.North + raster.South) / 2.0;
        double metersPerLonDegree = MetersPerLatDegree * Math.Cos(centerLat * Math.PI / 180.0);
        double halfWidthMeters = (raster.East - raster.West) * 0.5 * metersPerLonDegree;
        double halfHeightMeters = (raster.North - raster.South) * 0.5 * MetersPerLatDegree;

        var vertices = new Vector3[vertexCount];
        var normals = new Vector3[vertexCount];
        var colors = new uint[vertexCount];
        var indices = new ushort[(cols - 1) * (rows - 1) * 2 * 3];

        double cellWidthMeters = (cols > 1) ? ((raster.East - raster.West) / (cols - 1)) * metersPerLonDegree : 1.0;
        double cellHeightMeters = (rows > 1) ? ((raster.North - raster.South) / (rows - 1)) * MetersPerLatDegree : 1.0;

        // Build vertex positions. Row 0 = north edge = +Y; last row = south edge = -Y.
        for (int r = 0; r < rows; r++)
        {
            double yMeters = halfHeightMeters - (cellHeightMeters * r);
            for (int c = 0; c < cols; c++)
            {
                double xMeters = -halfWidthMeters + (cellWidthMeters * c);
                float z = raster[c, r] * options.VerticalExaggeration;
                vertices[(r * cols) + c] = new Vector3((float)xMeters, (float)yMeters, z);
            }
        }

        // Per-vertex normals via central differences in elevation.
        for (int r = 0; r < rows; r++)
        {
            int rN = Math.Max(r - 1, 0);
            int rS = Math.Min(r + 1, rows - 1);
            for (int c = 0; c < cols; c++)
            {
                int cW = Math.Max(c - 1, 0);
                int cE = Math.Min(c + 1, cols - 1);

                float zE = raster[cE, r] * options.VerticalExaggeration;
                float zW = raster[cW, r] * options.VerticalExaggeration;
                float zN = raster[c, rN] * options.VerticalExaggeration;
                float zS = raster[c, rS] * options.VerticalExaggeration;

                float dx = (float)((cE - cW) * cellWidthMeters);
                float dy = (float)((rS - rN) * cellHeightMeters);
                float dzdx = dx > 0f ? (zE - zW) / dx : 0f;
                // Row index grows southward, so dz/dy (north-positive) flips sign.
                float dzdy = dy > 0f ? (zN - zS) / dy : 0f;

                Vector3 normal = Vector3.Normalize(new Vector3(-dzdx, -dzdy, 1f));
                normals[(r * cols) + c] = normal;

                uint baseColor = HypsometricColor(raster[c, r]);
                float lambert = Math.Max(0f, Vector3.Dot(normal, options.LightDirection));
                float shade = options.AmbientFactor + ((1f - options.AmbientFactor) * lambert);
                colors[(r * cols) + c] = ApplyShade(baseColor, shade);
            }
        }

        // Two triangles per grid cell (clockwise as seen from +Z).
        int idx = 0;
        for (int r = 0; r < rows - 1; r++)
        {
            for (int c = 0; c < cols - 1; c++)
            {
                ushort i00 = (ushort)((r * cols) + c);
                ushort i10 = (ushort)((r * cols) + c + 1);
                ushort i01 = (ushort)(((r + 1) * cols) + c);
                ushort i11 = (ushort)(((r + 1) * cols) + c + 1);

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

        float horizontalExtent = (float)Math.Max(halfWidthMeters, halfHeightMeters);
        return new TerrainMesh3D(
            vertices,
            normals,
            colors,
            indices,
            Vector3.Zero,
            horizontalExtent,
            options.VerticalExaggeration,
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
