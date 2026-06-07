using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Krok 4c (NoData-aware mesh): a streamed detail patch may carry no-data cells (coverage gaps, uncached
/// tiles, the Slovak border). Building flat geometry over them produces the yellow "blinds" and the big
/// flat green rectangle. Instead, any triangle with a no-data corner is dropped — leaving a hole so the
/// coarse base shows through. Valid terrain is untouched.
/// </summary>
public sealed class TerrainMesh3DNoDataTests
{
    private static readonly MapBounds Bounds = new(new GeoPoint(49.0, 20.0), new GeoPoint(49.1, 20.1));
    private static readonly TerrainMeshOptions Opts = new() { VerticalExaggeration = 1f };

    [Fact]
    public void BuildTiles_AllValid_KeepsEveryTriangle()
    {
        var samples = new float[3 * 3];
        Array.Fill(samples, 100f);
        var raster = new DemRaster(3, 3, Bounds, samples, noDataValue: -9999f);

        TerrainMesh3D mesh = TerrainMesh3D.BuildTiles(raster, Opts)[0];

        // 3×3 grid = 4 quads × 2 triangles × 3 indices = 24.
        mesh.Indices.Length.Should().Be(24);
    }

    [Fact]
    public void BuildTiles_DropsOnlyTrianglesTouchingNoData()
    {
        // SE corner (col 2, row 2) is no-data. Only the one triangle whose corners include it is dropped.
        var samples = new float[]
        {
            100f, 100f, 100f,
            100f, 100f, 100f,
            100f, 100f, -9999f,
        };
        var raster = new DemRaster(3, 3, Bounds, samples, noDataValue: -9999f);

        TerrainMesh3D mesh = TerrainMesh3D.BuildTiles(raster, Opts)[0];

        // 8 triangles full; the SE quad's second triangle (NE, SE, SW) touches no-data → dropped ⇒ 7 ⇒ 21.
        mesh.Indices.Length.Should().Be(21);
    }

    [Fact]
    public void BuildTiles_AllNoData_ProducesNoTriangles()
    {
        var samples = new float[3 * 3];
        Array.Fill(samples, -9999f);
        var raster = new DemRaster(3, 3, Bounds, samples, noDataValue: -9999f);

        TerrainMesh3D mesh = TerrainMesh3D.BuildTiles(raster, Opts)[0];

        mesh.Indices.Length.Should().Be(0, "a fully no-data patch is a hole — the base carries it");
    }
}