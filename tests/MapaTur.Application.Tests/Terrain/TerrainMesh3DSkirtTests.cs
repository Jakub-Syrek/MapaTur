using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Skirt (Model 1 per-tile enabler): a vertical apron hung down from each tile's perimeter fills the crack
/// between adjacent tiles of different resolution with real terrain geometry, so seams don't show through.
/// </summary>
public sealed class TerrainMesh3DSkirtTests
{
    private static readonly MapBounds Bounds = new(new GeoPoint(49.0, 20.0), new GeoPoint(49.1, 20.1));

    private static DemRaster Flat(int side, float height)
    {
        var samples = new float[side * side];
        Array.Fill(samples, height);
        return new DemRaster(side, side, Bounds, samples);
    }

    [Fact]
    public void BuildTiles_NoSkirt_LeavesTheMeshUnchanged()
    {
        var options = new TerrainMeshOptions { VerticalExaggeration = 1f }; // SkirtDepthMeters defaults to 0

        TerrainMesh3D mesh = TerrainMesh3D.BuildTiles(Flat(3, 100f), options)[0];

        mesh.Vertices.Length.Should().Be(9, "3×3 grid, no skirt");
        mesh.Indices.Length.Should().Be(24, "4 quads × 2 triangles × 3");
    }

    [Fact]
    public void BuildTiles_WithSkirt_AppendsALoweredPerimeterRingAndWalls()
    {
        var options = new TerrainMeshOptions { VerticalExaggeration = 1f, SkirtDepthMeters = 10f };

        TerrainMesh3D mesh = TerrainMesh3D.BuildTiles(Flat(3, 100f), options)[0];

        // 3×3 has an 8-vertex perimeter ring → 8 skirt vertices appended (indices 9..16).
        mesh.Vertices.Length.Should().Be(17);
        for (int i = 9; i < 17; i++)
        {
            mesh.Vertices[i].Z.Should().BeApproximately(90f, 0.5f, "skirt hangs 10 m below the 100 m edge");
        }

        // 8 ring segments × 2 wall triangles × 3 = 48 extra indices on top of the 24 surface indices.
        mesh.Indices.Length.Should().Be(72);
    }

    [Fact]
    public void BuildTiles_SkirtDrop_ScalesWithVerticalExaggeration()
    {
        var options = new TerrainMeshOptions { VerticalExaggeration = 2f, SkirtDepthMeters = 10f };

        TerrainMesh3D mesh = TerrainMesh3D.BuildTiles(Flat(3, 100f), options)[0];

        // Edge sits at 100·2 = 200; the skirt drops 10·2 = 20 world units → 180.
        mesh.Vertices[9].Z.Should().BeApproximately(180f, 0.5f);
    }
}