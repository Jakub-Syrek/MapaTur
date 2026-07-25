using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour of <see cref="TerrainMeshOptions.NormalApronCells"/>: meshing a raster that carries a HALO of
/// neighbour cells around the region actually being drawn, so per-vertex normals at the drawn region's edge are
/// computed from REAL neighbour heights instead of the central difference clamping to the raster's own edge.
///
/// Why this exists: a baked pyramid tile is meshed as its OWN standalone raster, so the normal loop's clamp lands
/// on the TILE edge — the edge vertex gets a one-sided normal that disagrees with the adjacent tile's normal at
/// the very same ground point, even though the two tiles' heights there are bit-identical (welded by the baker).
/// The shader lights straight off that normal, so the disagreement renders as a bright/dark line along every tile
/// border — the "tile grid in the geometry", visible with the ortho overlay OFF on smooth slopes.
///
/// The apron is LOOK-ONLY plumbing: it changes which cells the normal loop may read, never a height, never an
/// emitted vertex position. Default 0 ⇒ every existing path meshes exactly as before.
/// </summary>
public sealed class TerrainMesh3DNormalApronTests
{
    private static readonly GeoPoint Anchor = new(49.2, 20.05);

    private const float Exaggeration = 1.5f;

    // Deliberately CURVED (quadratic + cross term). Curvature is what makes a clamped one-sided difference differ
    // from a true centred one: on a PLANE the clamp is harmless (halving the baseline halves the rise too), which
    // is exactly why this seam hides until the ground bends.
    private static float HeightAt(int c, int r) => 1000f + (3f * c * c) + (2f * r * r) + (c * r);

    private static DemRaster MakeRaster(int columns, int rows)
    {
        var heights = new float[columns * rows];
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < columns; c++)
            {
                heights[(r * columns) + c] = HeightAt(c, r);
            }
        }

        // Cell pitch is uniform across the fixtures below because bounds are derived from the cell COUNT: a
        // 5×5 raster and the 3×3 sub-raster of its interior then share one grid, so their vertices are comparable.
        const double pitch = 0.001;
        var southWest = new GeoPoint(49.30, 20.10);
        var northEast = new GeoPoint(49.30 + (pitch * (rows - 1)), 20.10 + (pitch * (columns - 1)));
        return new DemRaster(columns, rows, new MapBounds(southWest, northEast), heights, -9999f);
    }

    // The interior [1..cols-2] of MakeRaster(cols, rows) as a STANDALONE raster — i.e. what a baked tile is today:
    // its own little raster with no neighbour data, so its normal loop clamps on its own edge.
    private static DemRaster MakeInteriorRasterStandalone(int columns, int rows)
    {
        int innerCols = columns - 2;
        int innerRows = rows - 2;
        var heights = new float[innerCols * innerRows];
        for (int r = 0; r < innerRows; r++)
        {
            for (int c = 0; c < innerCols; c++)
            {
                heights[(r * innerCols) + c] = HeightAt(c + 1, r + 1);
            }
        }

        const double pitch = 0.001;
        var southWest = new GeoPoint(49.30 + pitch, 20.10 + pitch);
        var northEast = new GeoPoint(49.30 + (pitch * (rows - 2)), 20.10 + (pitch * (columns - 2)));
        return new DemRaster(innerCols, innerRows, new MapBounds(southWest, northEast), heights, -9999f);
    }

    private static TerrainMeshOptions Options(int apron = 0) => new()
    {
        VerticalExaggeration = Exaggeration,
        NormalApronCells = apron,
    };

    [Fact]
    public void Build_NormalApron_EmitsOnlyTheInteriorVertices()
    {
        DemRaster raster = MakeRaster(columns: 5, rows: 5);

        TerrainMesh3D mesh = TerrainMesh3D.Build(raster, Options(apron: 1), Anchor);

        // The 1-cell halo ring is read for normals but never drawn: 5×5 minus the ring = 3×3 = 9 vertices.
        // Drawing the ring would overlap the neighbour tile that owns that ground (double geometry, z-fighting).
        mesh.Vertices.Should().HaveCount(9);
    }

    [Fact]
    public void Build_NormalApron_KeepsInteriorVertexPositionsIdenticalToTheUnapronedBuild()
    {
        DemRaster raster = MakeRaster(columns: 5, rows: 5);
        TerrainMesh3D reference = TerrainMesh3D.Build(raster, Options(apron: 0), Anchor);

        TerrainMesh3D mesh = TerrainMesh3D.Build(raster, Options(apron: 1), Anchor);

        // The apron must move NOTHING. Every emitted vertex has to land exactly where the same ground point lands
        // without the apron — the accepted sub-1m geometry depends on it (and on tiles still welding at seams).
        var expected = new List<Vector3>();
        for (int r = 1; r <= 3; r++)
        {
            for (int c = 1; c <= 3; c++)
            {
                expected.Add(reference.Vertices[(r * 5) + c]);
            }
        }

        mesh.Vertices.Should().Equal(expected);
    }

    [Fact]
    public void Build_NormalApron_GivesEdgeVerticesTheTrueCentredNormalFromTheHalo()
    {
        DemRaster raster = MakeRaster(columns: 5, rows: 5);
        // In the full raster, cells [1..3] are INTERIOR: their normals are true centred differences, unclamped.
        // That is the ground truth an apron build of the same region must reproduce.
        TerrainMesh3D reference = TerrainMesh3D.Build(raster, Options(apron: 0), Anchor);

        TerrainMesh3D mesh = TerrainMesh3D.Build(raster, Options(apron: 1), Anchor);

        var expected = new List<Vector3>();
        for (int r = 1; r <= 3; r++)
        {
            for (int c = 1; c <= 3; c++)
            {
                expected.Add(reference.Normals[(r * 5) + c]);
            }
        }

        mesh.Normals.Should().Equal(expected);
    }

    [Fact]
    public void Build_WithoutApron_ClampsEdgeNormals_TheSeamThisFixTargets()
    {
        // Documents the ROOT CAUSE. A standalone interior raster (= a baked tile today) has no neighbour data, so
        // its border normals clamp to a one-sided difference and DISAGREE with the true normal at that same ground
        // point — which the neighbouring tile, meshing the same point as ITS interior, computes correctly. Two
        // different normals at one welded ground point = the bright/dark line along the tile border.
        DemRaster full = MakeRaster(columns: 5, rows: 5);
        DemRaster standalone = MakeInteriorRasterStandalone(columns: 5, rows: 5);

        TerrainMesh3D truth = TerrainMesh3D.Build(full, Options(apron: 0), Anchor);
        TerrainMesh3D clamped = TerrainMesh3D.Build(standalone, Options(apron: 0), Anchor);

        // Corner (0,0) of the standalone raster == cell (1,1) of the full raster: same ground, different normal.
        clamped.Normals[0].Should().NotBe(
            truth.Normals[(1 * 5) + 1],
            "a standalone tile's clamped border normal is exactly the seam being fixed");
    }

    [Fact]
    public void Build_NormalApron_ReportsTheInteriorBoundsNotTheHaloBounds()
    {
        DemRaster raster = MakeRaster(columns: 5, rows: 5);
        DemRaster interior = MakeInteriorRasterStandalone(columns: 5, rows: 5);

        TerrainMesh3D mesh = TerrainMesh3D.Build(raster, Options(apron: 1), Anchor);

        // The halo is scaffolding, not surface. Bounds must describe the ground actually drawn, or a consumer that
        // trusts them (ortho placement, base-skin coverage reasoning) would think this mesh owns a cell of ground
        // that its neighbour actually owns.
        mesh.Bounds.SouthWest.Longitude.Should().BeApproximately(interior.West, 1e-9);
        mesh.Bounds.NorthEast.Longitude.Should().BeApproximately(interior.East, 1e-9);
        mesh.Bounds.SouthWest.Latitude.Should().BeApproximately(interior.South, 1e-9);
        mesh.Bounds.NorthEast.Latitude.Should().BeApproximately(interior.North, 1e-9);
    }

    [Fact]
    public void Build_DefaultOptions_HaveNoApron()
    {
        // Every existing caller (live LOD base, per-tile detail, legacy) passes a raster with NO halo. If the
        // default were anything but 0 they would silently lose their outermost ring of ground.
        new TerrainMeshOptions().NormalApronCells.Should().Be(0);

        DemRaster raster = MakeRaster(columns: 5, rows: 5);
        TerrainMesh3D.Build(raster, new TerrainMeshOptions { VerticalExaggeration = Exaggeration }, Anchor)
            .Vertices.Should().HaveCount(25);
    }

    [Fact]
    public void Build_ApronLeavingFewerThanTwoInteriorCells_Throws()
    {
        DemRaster raster = MakeRaster(columns: 3, rows: 3);

        // 3 columns minus a 1-cell apron per side = 1 column of interior: nothing meshable. Fail loudly rather
        // than return a degenerate mesh.
        FluentActions.Invoking(() => TerrainMesh3D.Build(raster, Options(apron: 1), Anchor))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildTiles_NormalApron_CoversTheInteriorOnly()
    {
        DemRaster raster = MakeRaster(columns: 5, rows: 5);
        TerrainMesh3D reference = TerrainMesh3D.Build(raster, Options(apron: 0), Anchor);

        IReadOnlyList<TerrainMesh3D> tiles = TerrainMesh3D.BuildTiles(
            raster, Options(apron: 1), maxTileSide: 255, projectionAnchor: Anchor);

        // The tiled path is what the baked pyramid actually uses (BuildCut → BuildTiles), so the apron has to hold
        // there too: the union of the blocks must be the interior, never the halo ring.
        Vector3[] emitted = tiles.SelectMany(t => t.Vertices).ToArray();
        Vector3 haloCorner = reference.Vertices[0];
        emitted.Should().NotContain(haloCorner, "the halo ring is read for normals but never drawn");
        emitted.Should().Contain(reference.Vertices[(1 * 5) + 1]);
        emitted.Should().Contain(reference.Vertices[(3 * 5) + 3]);
    }
}
