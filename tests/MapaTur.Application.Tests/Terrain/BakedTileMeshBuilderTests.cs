using System.Linq;
using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour of <see cref="BakedTileMeshBuilder"/>: meshing ONE baked DEM tile at full resolution into a
/// <see cref="TerrainMesh3D"/> in the SAME world frame, vertex layout and ortho-UV / colour conventions as the
/// live terrain (so the existing shader draws it unchanged), with an optional downward border skirt that fills
/// LOD-seam cracks without touching the visible top surface. Pure — no GL, no IO.
/// </summary>
public sealed class BakedTileMeshBuilderTests
{
    // A Tatra-scale anchor shared by every build below, so world positions are expressed in one metric frame.
    private static readonly GeoPoint Anchor = new(49.2, 20.05);

    private const float Exaggeration = 1.5f;

    // A small baked tile whose geography sits a little NE of the anchor (so the anchor offset is non-zero and
    // the world-position check actually exercises it). Heights vary per cell so normals are non-trivial.
    private static BakedDemTile MakeTile(int columns = 3, int rows = 3, double noData = -9999.0)
    {
        var bounds = new MapBounds(new GeoPoint(49.30, 20.10), new GeoPoint(49.34, 20.16));
        var heights = new float[columns * rows];
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < columns; c++)
            {
                heights[(r * columns) + c] = 1000f + (c * 20f) + (r * 35f);
            }
        }

        return new BakedDemTile(zoom: 14, tileX: 9000, tileY: 5900, columns, rows, bounds, noData, heights);
    }

    private static TerrainMeshOptions Options() => new() { VerticalExaggeration = Exaggeration };

    // Same footprint/heights as MakeTile, but at the given ZOOM and carrying a UNIFORM DetailRms — isolates the
    // zoom→distance fade: any difference between two zooms' resulting mesh Detail must come from the fade, not
    // from different underlying residual data (which is identical here).
    private static BakedDemTile MakeTileWithDetail(int zoom, float rms, int columns = 3, int rows = 3, double noData = -9999.0)
    {
        var bounds = new MapBounds(new GeoPoint(49.30, 20.10), new GeoPoint(49.34, 20.16));
        var heights = new float[columns * rows];
        var detail = new float[columns * rows];
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < columns; c++)
            {
                heights[(r * columns) + c] = 1000f + (c * 20f) + (r * 35f);
                detail[(r * columns) + c] = rms;
            }
        }

        return new BakedDemTile(zoom, tileX: 9000, tileY: 5900, columns, rows, bounds, noData, heights, detail);
    }

    [Fact]
    public void Build_CoarserZoom_FadesDetailRelativeToFinerZoom()
    {
        // z13 is coarser than z15, so the quadtree LOD selector only ever picks it for GROUND FARTHER FROM THE
        // CAMERA. With identical raw RMS at both zooms, z13's synthetic bump must fade toward zero relative to
        // z15's — otherwise distant terrain ends up MORE textured than nearby terrain, backwards from what the
        // eye expects (and from what the user is asking for).
        BakedDemTile near = MakeTileWithDetail(zoom: 15, rms: 2.0f);
        BakedDemTile far = MakeTileWithDetail(zoom: 13, rms: 2.0f);

        float nearDetail = BakedTileMeshBuilder.Build(near, Anchor, Options()).Detail.Average();
        float farDetail = BakedTileMeshBuilder.Build(far, Anchor, Options()).Detail.Average();

        farDetail.Should().BeLessThan(nearDetail, "a coarser (farther) baked zoom must fade the synthetic detail, not amplify it");
    }

    [Fact]
    public void Build_NullTile_Throws()
    {
        FluentActions.Invoking(() => BakedTileMeshBuilder.Build(null!, Anchor))
            .Should().Throw<System.ArgumentNullException>();
    }

    [Fact]
    public void Build_NegativeSkirt_Throws()
    {
        BakedDemTile tile = MakeTile();
        FluentActions.Invoking(() => BakedTileMeshBuilder.Build(tile, Anchor, Options(), skirtDepthMeters: -1f))
            .Should().Throw<System.ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Build_KnownSmallTile_HasExpectedVertexAndIndexCounts()
    {
        BakedDemTile tile = MakeTile(columns: 3, rows: 3);

        TerrainMesh3D mesh = BakedTileMeshBuilder.Build(tile, Anchor, Options());

        // 3×3 grid → 9 top-surface vertices, 2 triangles per cell over the 2×2 cells = 8 triangles = 24 indices.
        mesh.Vertices.Should().HaveCount(9);
        mesh.Normals.Should().HaveCount(9);
        mesh.BaseColors.Should().HaveCount(9);
        mesh.TexCoords.Should().HaveCount(9 * 2);
        mesh.Indices.Should().HaveCount(8 * 3);
    }

    [Fact]
    public void Build_VertexPositions_MatchGeoToWorldAboutTheSharedAnchor()
    {
        BakedDemTile tile = MakeTile(columns: 3, rows: 3);
        DemRaster raster = BakedTileMeshBuilder.AsRaster(tile);

        TerrainMesh3D mesh = BakedTileMeshBuilder.Build(tile, Anchor, Options());

        // Check several sample cells: row 0 = north edge, column 0 = west edge (DemRaster convention). The vertex
        // for cell (col,row) must equal GeoToWorld of that cell's geographic position about the SAME anchor and
        // exaggeration — i.e. baked tiles land exactly where the rest of the LOD frame would put the same ground.
        (int Col, int Row)[] cells = { (0, 0), (2, 0), (0, 2), (2, 2), (1, 1) };
        foreach ((int col, int row) in cells)
        {
            double lon = raster.West + ((double)col / (raster.Columns - 1) * (raster.East - raster.West));
            double lat = raster.North - ((double)row / (raster.Rows - 1) * (raster.North - raster.South));
            float elevation = raster[col, row];
            Vector3 expected = LocalTangentProjection.GeoToWorld(new GeoPoint(lat, lon), elevation, Anchor, Exaggeration);

            Vector3 actual = mesh.Vertices[(row * raster.Columns) + col];
            actual.X.Should().BeApproximately(expected.X, 0.5f);
            actual.Y.Should().BeApproximately(expected.Y, 0.5f);
            actual.Z.Should().BeApproximately(expected.Z, 1e-3f);
        }
    }

    [Fact]
    public void Build_Normals_AreFiniteUnitAndUpward()
    {
        BakedDemTile tile = MakeTile(columns: 4, rows: 4);

        TerrainMesh3D mesh = BakedTileMeshBuilder.Build(tile, Anchor, Options());

        foreach (Vector3 n in mesh.Normals)
        {
            float.IsFinite(n.X).Should().BeTrue();
            float.IsFinite(n.Y).Should().BeTrue();
            float.IsFinite(n.Z).Should().BeTrue();
            n.Length().Should().BeApproximately(1f, 1e-3f); // unit length
            n.Z.Should().BeGreaterThan(0f);                 // a height-field normal always points up
        }
    }

    [Fact]
    public void Build_WithSkirt_KeepsTopSurfaceVerticesIdenticalToNoSkirtBuild()
    {
        BakedDemTile tile = MakeTile(columns: 3, rows: 3);

        TerrainMesh3D flat = BakedTileMeshBuilder.Build(tile, Anchor, Options());
        TerrainMesh3D skirted = BakedTileMeshBuilder.Build(tile, Anchor, Options(), skirtDepthMeters: 50f);

        // The top surface is emitted first, so the leading vertexCount entries must be byte-identical: the skirt
        // only appends, never perturbs, the visible surface. (Compare exact bits, not approximately.)
        int topCount = flat.Vertices.Length;
        skirted.Vertices.Length.Should().BeGreaterThan(topCount); // skirt added something
        for (int i = 0; i < topCount; i++)
        {
            skirted.Vertices[i].Should().Be(flat.Vertices[i]);
            skirted.Normals[i].Should().Be(flat.Normals[i]);
            skirted.BaseColors[i].Should().Be(flat.BaseColors[i]);
        }
    }

    [Fact]
    public void Build_WithSkirt_AddsBorderSkirtVerticesDroppedByExaggeratedSkirtDepth()
    {
        BakedDemTile tile = MakeTile(columns: 3, rows: 3);
        const float skirt = 50f;

        TerrainMesh3D flat = BakedTileMeshBuilder.Build(tile, Anchor, Options());
        TerrainMesh3D skirted = BakedTileMeshBuilder.Build(tile, Anchor, Options(), skirtDepthMeters: skirt);

        // A 3×3 tile's border ring is all 8 perimeter vertices → 8 appended skirt vertices.
        int topCount = flat.Vertices.Length;
        int skirtCount = skirted.Vertices.Length - topCount;
        skirtCount.Should().Be(8);

        // Each skirt vertex is a copy of some TOP border vertex pushed straight down by skirtDepth × exaggeration
        // (X/Y unchanged) — it fills the vertical gap to a finer neighbour without altering the top surface.
        float drop = skirt * Exaggeration;
        var topByXy = skirted.Vertices.Take(topCount).ToList();
        for (int i = topCount; i < skirted.Vertices.Length; i++)
        {
            Vector3 s = skirted.Vertices[i];
            Vector3? match = topByXy
                .Where(t => t.X == s.X && t.Y == s.Y)
                .Select(t => (Vector3?)t)
                .FirstOrDefault();
            match.Should().NotBeNull("each skirt vertex sits directly under a top border vertex");
            s.Z.Should().BeApproximately(match!.Value.Z - drop, 1e-3f);
        }
    }

    [Fact]
    public void Build_IsDeterministic()
    {
        BakedDemTile tile = MakeTile(columns: 4, rows: 4);

        TerrainMesh3D a = BakedTileMeshBuilder.Build(tile, Anchor, Options(), skirtDepthMeters: 30f);
        TerrainMesh3D b = BakedTileMeshBuilder.Build(tile, Anchor, Options(), skirtDepthMeters: 30f);

        a.Vertices.Should().Equal(b.Vertices);
        a.Normals.Should().Equal(b.Normals);
        a.Indices.Should().Equal(b.Indices);
        a.BaseColors.Should().Equal(b.BaseColors);
        a.TexCoords.Should().Equal(b.TexCoords);
    }

    [Fact]
    public void Build_BakedTileAndEquivalentRaster_ProduceTheSameTopSurface()
    {
        BakedDemTile tile = MakeTile(columns: 4, rows: 4);
        DemRaster raster = BakedTileMeshBuilder.AsRaster(tile);

        TerrainMesh3D fromTile = BakedTileMeshBuilder.Build(tile, Anchor, Options());
        // Mesh the equivalent raster directly via the shared-anchor TerrainMesh3D factory — same world frame.
        TerrainMesh3D fromRaster = TerrainMesh3D.Build(raster, Options(), Anchor);

        fromTile.Vertices.Should().Equal(fromRaster.Vertices);
        fromTile.Normals.Should().Equal(fromRaster.Normals);
        fromTile.Indices.Should().Equal(fromRaster.Indices);
        fromTile.BaseColors.Should().Equal(fromRaster.BaseColors);
    }

    [Fact]
    public void BuildCut_NoOrtho_ReturnsASingleMeshEqualToBuild()
    {
        BakedDemTile tile = MakeTile(columns: 5, rows: 5);

        IReadOnlyList<TerrainMesh3D> cut = BakedTileMeshBuilder.BuildCut(tile, Anchor, Options());
        TerrainMesh3D single = BakedTileMeshBuilder.Build(tile, Anchor, Options());

        cut.Should().HaveCount(1, "with no ortho there is no cell boundary to cut at");
        cut[0].Vertices.Should().Equal(single.Vertices);
        cut[0].Indices.Should().Equal(single.Indices);
    }

    [Fact]
    public void BuildCut_TileInsideOneOrthoCell_ReturnsASingleMesh()
    {
        BakedDemTile tile = MakeTile(columns: 9, rows: 9);
        DemRaster raster = BakedTileMeshBuilder.AsRaster(tile);

        // A coverage MUCH larger than the tile with few cells ⇒ the tile sits well inside one cell ⇒ no cut.
        var coverage = new OrthoCoverage(
            new MapBounds(new GeoPoint(49.0, 19.8), new GeoPoint(49.6, 20.6)), GridCols: 2, GridRows: 2);
        coverage.Covers(new GeoPoint((raster.North + raster.South) / 2, (raster.East + raster.West) / 2))
            .Should().BeTrue();

        IReadOnlyList<TerrainMesh3D> cut = BakedTileMeshBuilder.BuildCut(tile, Anchor, Options(), orthoCoverage: coverage);

        cut.Should().HaveCount(1, "a tile inside a single ortho cell needs no sub-meshing");
    }

    [Fact]
    public void BuildCut_TileStraddlingAnOrthoCellBoundary_CutsIntoPerCellSubMeshesWithDistinctCellIndices()
    {
        // Tile spans lon [20.10, 20.16] × lat [49.30, 49.34]. A coverage whose cell boundaries fall INSIDE that
        // span forces a cut so no sub-mesh straddles a cell (else its far-side UV clamps → strata stripes).
        BakedDemTile tile = MakeTile(columns: 17, rows: 17);
        DemRaster raster = BakedTileMeshBuilder.AsRaster(tile);

        // 6 cols × 4 rows over a coverage tightly bracketing the tile ⇒ several boundaries cross the tile.
        var coverage = new OrthoCoverage(
            new MapBounds(new GeoPoint(49.28, 20.08), new GeoPoint(49.36, 20.18)), GridCols: 6, GridRows: 4);

        IReadOnlyList<TerrainMesh3D> cut = BakedTileMeshBuilder.BuildCut(tile, Anchor, Options(), orthoCoverage: coverage);

        cut.Count.Should().BeGreaterThan(1, "a tile straddling ortho cell boundaries must be cut into per-cell blocks");
        cut.Select(m => m.OrthoTileIndex).Distinct().Count().Should()
            .BeGreaterThan(1, "the sub-meshes must address different ortho cells (so each samples its own texture)");

        // The cut must be loss-free: the union of sub-mesh top-surface triangles covers the same ground as one
        // uncut block would (every interior cell of the tile is still triangulated somewhere).
        TerrainMesh3D whole = BakedTileMeshBuilder.Build(tile, Anchor, Options(), orthoCoverage: coverage);
        int cutTris = cut.Sum(m => CountTopTriangles(m));
        CountTopTriangles(whole).Should().Be(cutTris, "cutting at cell boundaries must not drop or duplicate surface");
    }

    // Triangles whose three vertices are all top-surface (index < the mesh's surface vertex count). Skirts append
    // vertices after the surface, so this counts only the visible top surface for a like-for-like comparison.
    private static int CountTopTriangles(TerrainMesh3D mesh)
    {
        // With no skirt every vertex is top-surface, so all triangles count; kept general for safety.
        int surface = mesh.Vertices.Length;
        int tris = 0;
        for (int i = 0; i + 2 < mesh.Indices.Length; i += 3)
        {
            if (mesh.Indices[i] < surface && mesh.Indices[i + 1] < surface && mesh.Indices[i + 2] < surface)
            {
                tris++;
            }
        }

        return tris;
    }

    [Fact]
    public void Build_NoDataCell_DropsTrianglesTouchingItButKeepsTheRest()
    {
        // Punch a hole at the centre of a 3×3 tile. A mesh that holes through NoData (the TerrainMesh3D
        // convention this REUSES, not reinvents) drops only the triangles whose corners touch the hole, leaving
        // the others — so the index buffer shrinks but isn't empty, while the surface vertex array stays full
        // size. This pins that we inherit NoData behaviour from TerrainMesh3D rather than handling it ourselves.
        const double noData = -9999.0;
        BakedDemTile full = MakeTile(columns: 3, rows: 3, noData: noData);
        BakedDemTile holed = MakeTile(columns: 3, rows: 3, noData: noData);
        holed.Heights[(1 * 3) + 1] = (float)noData;

        TerrainMesh3D fullMesh = BakedTileMeshBuilder.Build(full, Anchor, Options());
        TerrainMesh3D holedMesh = BakedTileMeshBuilder.Build(holed, Anchor, Options());

        holedMesh.Vertices.Should().HaveCount(9);                              // surface grid unchanged
        holedMesh.Indices.Length.Should().BeLessThan(fullMesh.Indices.Length); // some triangles dropped
        holedMesh.Indices.Should().NotBeEmpty();                               // but not all — the hole is local
    }
}