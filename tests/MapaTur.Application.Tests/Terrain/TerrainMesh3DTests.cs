using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class TerrainMesh3DTests
{
    private static DemRaster BuildFlatRaster(int cols, int rows, float elevation = 1000f)
    {
        var samples = new float[cols * rows];
        Array.Fill(samples, elevation);
        var bounds = new MapBounds(
            new GeoPoint(49.0, 19.0),
            new GeoPoint(50.0, 20.0));
        return new DemRaster(cols, rows, bounds, samples);
    }

    [Fact]
    public void Build_FromTwoByTwoRaster_ProducesFourVerticesAndTwoTriangles()
    {
        var raster = BuildFlatRaster(2, 2);

        TerrainMesh3D mesh = TerrainMesh3D.Build(raster);

        mesh.Vertices.Length.Should().Be(4);
        mesh.Colors.Length.Should().Be(4);
        mesh.Normals.Length.Should().Be(4);
        mesh.Indices.Length.Should().Be(6, "two triangles × three indices each");
    }

    private static (byte R, byte G, byte B) Rgb(uint argb)
        => ((byte)((argb >> 16) & 0xFF), (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF));

    [Fact]
    public void Build_BaseColorsAreUnshadedHypsometric()
    {
        var raster = BuildFlatRaster(2, 2, elevation: 1000f);

        TerrainMesh3D mesh = TerrainMesh3D.Build(raster);

        mesh.BaseColors.Length.Should().Be(4);
        mesh.BaseColors[0].Should().Be(TerrainMesh3D.HypsometricColor(1000f),
            "the base colour is the pure elevation tint, with no Lambert shading baked in (the GPU shades per-pixel)");
    }

    [Fact]
    public void Build_ColorsAreShadedDarkerThanBaseColors()
    {
        // A flat raster still shades below full brightness: lambert(dot(up, NW-sun)) < 1, so the
        // baked per-vertex colour is darker than the unshaded base on every channel.
        var raster = BuildFlatRaster(2, 2, elevation: 1000f);

        TerrainMesh3D mesh = TerrainMesh3D.Build(raster);

        var (br, bg, bb) = Rgb(mesh.BaseColors[0]);
        var (sr, sg, sb) = Rgb(mesh.Colors[0]);
        mesh.Colors[0].Should().NotBe(mesh.BaseColors[0]);
        sr.Should().BeLessThanOrEqualTo(br);
        sg.Should().BeLessThanOrEqualTo(bg);
        sb.Should().BeLessThanOrEqualTo(bb);
    }

    [Fact]
    public void Build_ExposesLightDirectionAndAmbientFromOptions()
    {
        var raster = BuildFlatRaster(2, 2);
        var opts = new TerrainMeshOptions
        {
            LightDirection = Vector3.Normalize(new Vector3(1f, 0f, 1f)),
            AmbientFactor = 0.5f,
        };

        TerrainMesh3D mesh = TerrainMesh3D.Build(raster, opts);

        mesh.LightDirection.X.Should().BeApproximately(opts.LightDirection.X, 1e-5f);
        mesh.LightDirection.Y.Should().BeApproximately(opts.LightDirection.Y, 1e-5f);
        mesh.LightDirection.Z.Should().BeApproximately(opts.LightDirection.Z, 1e-5f);
        mesh.AmbientFactor.Should().Be(0.5f);
    }

    [Fact]
    public void Build_VertexAtNorthwestCorner_HasNegativeXPositiveY()
    {
        var raster = BuildFlatRaster(2, 2, elevation: 1500f);

        TerrainMesh3D mesh = TerrainMesh3D.Build(raster);

        // Row 0, column 0 = north-west corner. Center is bbox center, so NW is -X +Y in world meters.
        Vector3 nw = mesh.Vertices[0];
        nw.X.Should().BeLessThan(0f);
        nw.Y.Should().BeGreaterThan(0f);
    }

    [Fact]
    public void Build_ZCoordinateScalesWithVerticalExaggeration()
    {
        var raster = BuildFlatRaster(2, 2, elevation: 1000f);
        var opts1 = new TerrainMeshOptions { VerticalExaggeration = 1.0f };
        var opts2 = new TerrainMeshOptions { VerticalExaggeration = 3.0f };

        TerrainMesh3D mesh1 = TerrainMesh3D.Build(raster, opts1);
        TerrainMesh3D mesh2 = TerrainMesh3D.Build(raster, opts2);

        float z1 = mesh1.Vertices[0].Z;
        float z2 = mesh2.Vertices[0].Z;
        z2.Should().BeApproximately(3f * z1, 1e-3f);
    }

    [Fact]
    public void Build_AllIndicesAreValid()
    {
        var raster = BuildFlatRaster(4, 3);

        TerrainMesh3D mesh = TerrainMesh3D.Build(raster);

        foreach (ushort index in mesh.Indices)
        {
            index.Should().BeLessThan((ushort)mesh.Vertices.Length);
        }
    }

    [Fact]
    public void Build_TriangleCountMatchesGridCells()
    {
        var raster = BuildFlatRaster(5, 4);

        TerrainMesh3D mesh = TerrainMesh3D.Build(raster);

        // (cols - 1) × (rows - 1) cells, each split into 2 triangles.
        int expectedTriangles = (5 - 1) * (4 - 1) * 2;
        (mesh.Indices.Length / 3).Should().Be(expectedTriangles);
    }

    [Fact]
    public void Build_NormalsArePointingUpForFlatTerrain()
    {
        var raster = BuildFlatRaster(3, 3);

        TerrainMesh3D mesh = TerrainMesh3D.Build(raster);

        foreach (Vector3 normal in mesh.Normals)
        {
            normal.Z.Should().BeApproximately(1f, 1e-3f);
            normal.X.Should().BeApproximately(0f, 1e-3f);
            normal.Y.Should().BeApproximately(0f, 1e-3f);
        }
    }

    [Theory]
    [InlineData(500.0)]   // Lowland — should be greenish.
    [InlineData(2500.0)]  // Peak — should be near-white.
    public void HypsometricColor_ReturnsOpaqueArgb(double elevationMeters)
    {
        uint argb = TerrainMesh3D.HypsometricColor(elevationMeters);

        uint alpha = (argb >> 24) & 0xFF;
        alpha.Should().Be(0xFF);
    }

    [Fact]
    public void HypsometricColor_LowElevationIsGreener()
    {
        uint low = TerrainMesh3D.HypsometricColor(700.0);

        uint r = (low >> 16) & 0xFF;
        uint g = (low >> 8) & 0xFF;
        g.Should().BeGreaterThan(r, "low elevations are green");
    }

    [Fact]
    public void Build_FromTatryScaleRaster_SucceedsWhenVertexCountFitsUshort()
    {
        // 256×86 = 22 016 vertices (well under 65 536), but the index buffer is
        // 255×85×2×3 = 130 050 ushorts long. Long buffer is fine; only the *values*
        // must fit in ushort. The validation must gate on vertex count, not buffer length.
        var raster = BuildFlatRaster(256, 86);

        Action act = () => TerrainMesh3D.Build(raster);

        act.Should().NotThrow();
    }

    [Fact]
    public void Build_WhenVertexCountExceedsUshort_Throws()
    {
        // 257×256 = 65 792 vertices — exceeds ushort.MaxValue + 1.
        var raster = BuildFlatRaster(257, 256);

        Action act = () => TerrainMesh3D.Build(raster);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildTiles_RasterWithinLimit_ReturnsSingleTileMatchingBuild()
    {
        var raster = BuildFlatRaster(8, 8, elevation: 1200f);

        var tiles = TerrainMesh3D.BuildTiles(raster);
        TerrainMesh3D single = TerrainMesh3D.Build(raster);

        tiles.Should().HaveCount(1);
        tiles[0].Vertices.Length.Should().Be(single.Vertices.Length);
        tiles[0].Vertices[0].Should().Be(single.Vertices[0]);
        tiles[0].Indices.Length.Should().Be(single.Indices.Length);
    }

    [Fact]
    public void BuildTiles_RasterLargerThanUshortLimit_SplitsIntoTilesEachWithinLimit()
    {
        // 400×400 = 160 000 vertices — far past the 65 536 single-mesh cap that Build rejects.
        var raster = BuildFlatRaster(400, 400);

        var tiles = TerrainMesh3D.BuildTiles(raster);

        tiles.Count.Should().BeGreaterThan(1);
        foreach (var tile in tiles)
        {
            tile.Vertices.Length.Should().BeLessThanOrEqualTo(ushort.MaxValue + 1);
            foreach (ushort index in tile.Indices)
            {
                // int compare — a full 65 536-vertex tile uses indices 0..65535, and (ushort)65536 wraps to 0.
                ((int)index).Should().BeLessThan(tile.Vertices.Length);
            }
        }
    }

    [Fact]
    public void BuildTiles_AdjacentTiles_ShareSeamVertexPositions()
    {
        // 300 columns > 255 → splits horizontally at column 255. Both tiles must place that seam
        // column at the identical world position, or the surface shows a crack between them.
        var raster = BuildFlatRaster(300, 4, elevation: 1500f);

        var tiles = TerrainMesh3D.BuildTiles(raster);

        tiles.Should().HaveCount(2);
        // tile 0 covers columns [0..255] (tileCols = 256); its last column (local index 255 in row 0)
        // is the seam. tile 1 covers [255..299]; its first column (local index 0 in row 0) is the seam.
        Vector3 seamFromTile0 = tiles[0].Vertices[255];
        Vector3 seamFromTile1 = tiles[1].Vertices[0];
        seamFromTile1.Should().Be(seamFromTile0);
    }

    [Fact]
    public void BuildTiles_EveryTileCarriesFullRasterBoundsAndExaggeration()
    {
        var raster = BuildFlatRaster(400, 300);
        var opts = new TerrainMeshOptions { VerticalExaggeration = 2.5f };

        var tiles = TerrainMesh3D.BuildTiles(raster, opts);

        foreach (var tile in tiles)
        {
            tile.Bounds.Should().Be(raster.Bounds);
            tile.VerticalExaggeration.Should().Be(2.5f);
        }
    }

    [Fact]
    public void BuildTiles_TileTrianglesSumToWholeGridWithNoDoubleCounting()
    {
        var raster = BuildFlatRaster(300, 300);

        var tiles = TerrainMesh3D.BuildTiles(raster);

        int totalTriangles = tiles.Sum(t => t.Indices.Length / 3);
        totalTriangles.Should().Be((300 - 1) * (300 - 1) * 2);
    }

    [Fact]
    public void BuildTiles_RejectsNonPositiveMaxTileSide()
    {
        var raster = BuildFlatRaster(4, 4);

        Action act = () => TerrainMesh3D.BuildTiles(raster, maxTileSide: 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Build_ExposesVerticalExaggerationFromOptions()
    {
        var raster = BuildFlatRaster(2, 2);
        var opts = new TerrainMeshOptions { VerticalExaggeration = 3.5f };

        TerrainMesh3D mesh = TerrainMesh3D.Build(raster, opts);

        mesh.VerticalExaggeration.Should().Be(3.5f);
    }

    [Fact]
    public void GeoToWorld_AtBboxCenter_ReturnsOriginXY()
    {
        var raster = BuildFlatRaster(2, 2);
        TerrainMesh3D mesh = TerrainMesh3D.Build(raster);
        var centerLat = (raster.North + raster.South) / 2.0;
        var centerLon = (raster.East + raster.West) / 2.0;

        Vector3 world = mesh.GeoToWorld(new GeoPoint(centerLat, centerLon), elevationMeters: 0f);

        world.X.Should().BeApproximately(0f, 1f);
        world.Y.Should().BeApproximately(0f, 1f);
    }

    [Fact]
    public void GeoToWorld_PointEastOfCenter_HasPositiveX()
    {
        var raster = BuildFlatRaster(2, 2);
        TerrainMesh3D mesh = TerrainMesh3D.Build(raster);
        var centerLat = (raster.North + raster.South) / 2.0;
        // raster spans west..east = 19..20, so 19.8 is east of center 19.5.
        var p = new GeoPoint(centerLat, 19.8);

        Vector3 world = mesh.GeoToWorld(p, elevationMeters: 0f);

        world.X.Should().BeGreaterThan(0f);
    }

    [Fact]
    public void GeoToWorld_PointNorthOfCenter_HasPositiveY()
    {
        var raster = BuildFlatRaster(2, 2);
        TerrainMesh3D mesh = TerrainMesh3D.Build(raster);
        var centerLon = (raster.East + raster.West) / 2.0;
        // raster spans south..north = 49..50, so 49.8 is north of center 49.5.
        var p = new GeoPoint(49.8, centerLon);

        Vector3 world = mesh.GeoToWorld(p, elevationMeters: 0f);

        world.Y.Should().BeGreaterThan(0f);
    }

    [Fact]
    public void GeoToWorld_ElevationIsScaledByVerticalExaggeration()
    {
        var raster = BuildFlatRaster(2, 2);
        var opts = new TerrainMeshOptions { VerticalExaggeration = 4f };
        TerrainMesh3D mesh = TerrainMesh3D.Build(raster, opts);
        var centerLat = (raster.North + raster.South) / 2.0;
        var centerLon = (raster.East + raster.West) / 2.0;

        Vector3 world = mesh.GeoToWorld(new GeoPoint(centerLat, centerLon), elevationMeters: 100f);

        world.Z.Should().BeApproximately(400f, 1e-3f);
    }

    [Fact]
    public void WorldToGeo_AtOrigin_ReturnsBboxCenter()
    {
        var raster = BuildFlatRaster(2, 2);
        TerrainMesh3D mesh = TerrainMesh3D.Build(raster);
        var centerLat = (raster.North + raster.South) / 2.0;
        var centerLon = (raster.East + raster.West) / 2.0;

        GeoPoint geo = mesh.WorldToGeo(Vector3.Zero);

        geo.Latitude.Should().BeApproximately(centerLat, 1e-6);
        geo.Longitude.Should().BeApproximately(centerLon, 1e-6);
    }

    [Fact]
    public void WorldToGeo_PositiveX_IsEastOfCenter()
    {
        var raster = BuildFlatRaster(2, 2);
        TerrainMesh3D mesh = TerrainMesh3D.Build(raster);
        var centerLon = (raster.East + raster.West) / 2.0;

        GeoPoint geo = mesh.WorldToGeo(new Vector3(5000f, 0f, 0f));

        geo.Longitude.Should().BeGreaterThan(centerLon);
    }

    [Fact]
    public void WorldToGeo_PositiveY_IsNorthOfCenter()
    {
        var raster = BuildFlatRaster(2, 2);
        TerrainMesh3D mesh = TerrainMesh3D.Build(raster);
        var centerLat = (raster.North + raster.South) / 2.0;

        GeoPoint geo = mesh.WorldToGeo(new Vector3(0f, 5000f, 0f));

        geo.Latitude.Should().BeGreaterThan(centerLat);
    }

    [Fact]
    public void WorldToGeo_RoundTripsWithGeoToWorld()
    {
        var raster = BuildFlatRaster(2, 2);
        TerrainMesh3D mesh = TerrainMesh3D.Build(raster);
        // An off-centre point well inside the raster bbox (spans 49..50, 19..20).
        var original = new GeoPoint(49.7, 19.3);

        Vector3 world = mesh.GeoToWorld(original, elevationMeters: 0f);
        GeoPoint roundTripped = mesh.WorldToGeo(world);

        roundTripped.Latitude.Should().BeApproximately(original.Latitude, 1e-4);
        roundTripped.Longitude.Should().BeApproximately(original.Longitude, 1e-4);
    }

    [Fact]
    public void WorldToGeo_IgnoresElevationComponent()
    {
        var raster = BuildFlatRaster(2, 2);
        TerrainMesh3D mesh = TerrainMesh3D.Build(raster);

        GeoPoint atZeroZ = mesh.WorldToGeo(new Vector3(1000f, 2000f, 0f));
        GeoPoint atHighZ = mesh.WorldToGeo(new Vector3(1000f, 2000f, 9999f));

        atHighZ.Latitude.Should().BeApproximately(atZeroZ.Latitude, 1e-9);
        atHighZ.Longitude.Should().BeApproximately(atZeroZ.Longitude, 1e-9);
    }

    [Fact]
    public void HypsometricColor_HighElevationIsLight()
    {
        uint high = TerrainMesh3D.HypsometricColor(2500.0);

        uint r = (high >> 16) & 0xFF;
        uint g = (high >> 8) & 0xFF;
        uint b = high & 0xFF;
        r.Should().BeGreaterThan(200);
        g.Should().BeGreaterThan(200);
        b.Should().BeGreaterThan(200);
    }
}