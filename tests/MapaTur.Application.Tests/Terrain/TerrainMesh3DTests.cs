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
