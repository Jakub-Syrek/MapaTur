using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Crack-free per-tile detail (Model 1). The old path cropped each tile and subsampled it from the crop's own
/// (0,0), so adjacent tiles of different steps put their shared edge at different world positions → see-through
/// cracks. <see cref="TerrainMesh3D.BuildAdaptiveTiles"/> builds every tile from the SAME full raster at absolute
/// strided cells, so neighbours share their boundary vertices exactly, and welds a finer edge to a coarser
/// neighbour's anchors (no T-junction).
/// </summary>
public sealed class TerrainMesh3DAdaptiveTests
{
    private static readonly MapBounds Bounds = new(new GeoPoint(49.0, 20.0), new GeoPoint(49.1, 20.1));

    private static DemRaster Ramp(int side)
    {
        var samples = new float[side * side];
        for (int r = 0; r < side; r++)
        {
            for (int c = 0; c < side; c++)
            {
                samples[(r * side) + c] = 100f + (c * 3f) + r; // varied so positions are non-trivial
            }
        }

        return new DemRaster(side, side, Bounds, samples);
    }

    [Fact]
    public void BuildAdaptiveTiles_AdjacentTiles_ShareBoundaryVerticesExactly()
    {
        DemRaster full = Ramp(9);
        var plan = new List<PerTileLodDecision>
        {
            new(0, 0, 4, 9, 1), // left tile owns cols 0..3; the builder reads one more → cols 0..4
            new(4, 0, 4, 9, 1), // right tile owns cols 4..7; reads → cols 4..8 → shares col 4 with the left tile
        };

        IReadOnlyList<TerrainMesh3D> meshes = TerrainMesh3D.BuildAdaptiveTiles(
            full, plan, new TerrainMeshOptions { VerticalExaggeration = 1f });

        TerrainMesh3D left = meshes[0];
        TerrainMesh3D right = meshes[1];
        for (int row = 0; row < 9; row++)
        {
            Vector3 leftEdge = left.Vertices[(row * 5) + 4];  // left tile's east column
            Vector3 rightEdge = right.Vertices[(row * 5) + 0]; // right tile's west column
            leftEdge.Should().Be(rightEdge, $"row {row}: the shared boundary vertex must coincide exactly (no gap)");
        }
    }

    [Fact]
    public void BuildAdaptiveTiles_Step1_KeepsEveryCellLikeAFullMesh()
    {
        DemRaster full = Ramp(6);
        var plan = new List<PerTileLodDecision> { new(0, 0, 6, 6, 1) };

        TerrainMesh3D mesh = TerrainMesh3D.BuildAdaptiveTiles(
            full, plan, new TerrainMeshOptions { VerticalExaggeration = 1f })[0];

        mesh.Vertices.Length.Should().Be(36, "step 1 over a 6×6 crop = every cell");
    }

    [Fact]
    public void BuildAdaptiveTiles_CoarserNeighbourEdge_WeldsInBetweenVerticesAway()
    {
        // A step-1 tile whose EAST neighbour is step 2 (EdgeStepEast=2) must WELD its east-edge in-between rows to
        // the every-2 anchors the coarse neighbour also has — so no triangle references the odd east-edge rows
        // (T-junction removed). 5×5 tile: east edge is col 4, rows 0..4; odd rows 1,3 are welded.
        DemRaster full = Ramp(5);
        var plan = new List<PerTileLodDecision> { new(0, 0, 5, 5, 1, EdgeStepEast: 2) };

        TerrainMesh3D mesh = TerrainMesh3D.BuildAdaptiveTiles(
            full, plan, new TerrainMeshOptions { VerticalExaggeration = 1f })[0];

        // East-edge odd-row vertices (col 4, rows 1 and 3) → flat indices 9 and 19 — welded, so unreferenced.
        mesh.Indices.Should().NotContain((ushort)9, "in-between east-edge vertex welds to an anchor");
        mesh.Indices.Should().NotContain((ushort)19, "in-between east-edge vertex welds to an anchor");
        mesh.Indices.Should().Contain((ushort)4).And.Contain((ushort)14).And.Contain((ushort)24,
            "the east-edge anchors (shared with the coarse neighbour) stay referenced");
    }
}
