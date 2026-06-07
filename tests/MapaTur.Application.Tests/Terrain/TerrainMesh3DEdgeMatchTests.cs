using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Krok 4c (edge matching): a streamed detail patch sits over a coarse base. Without help, the patch's
/// outer ring is at the detail's (finer, different) heights while the base under it is at coarse heights,
/// so the perimeter steps away from the base — the "hard boundary". Passing the base as an edge-height
/// source pins the patch's outer perimeter vertices to the base surface, so the patch meets the base
/// seamlessly; the interior keeps full detail.
/// </summary>
public sealed class TerrainMesh3DEdgeMatchTests
{
    private static readonly MapBounds Bounds = new(new GeoPoint(49.0, 20.0), new GeoPoint(49.1, 20.1));

    private static DemRaster Flat(int side, double heightMeters)
    {
        var samples = new float[side * side];
        Array.Fill(samples, (float)heightMeters);
        return new DemRaster(side, side, Bounds, samples);
    }

    [Fact]
    public void BuildTiles_WithEdgeHeightSource_PinsOuterPerimeterToBase_KeepsInteriorDetail()
    {
        DemRaster baseLayer = Flat(4, 100.0);  // coarse base under the patch
        DemRaster detail = Flat(4, 200.0);     // finer detail, higher here
        var options = new TerrainMeshOptions { VerticalExaggeration = 1f };

        TerrainMesh3D mesh = TerrainMesh3D.BuildTiles(detail, options, edgeHeightSource: baseLayer)[0];

        // 4×4 grid, row-major. Outer ring (rows 0 & 3, cols 0 & 3) is perimeter; indices 5/6/9/10 interior.
        mesh.Vertices[0].Z.Should().BeApproximately(100f, 0.5f, "NW corner pinned to the base height");
        mesh.Vertices[3].Z.Should().BeApproximately(100f, 0.5f, "NE corner pinned");
        mesh.Vertices[12].Z.Should().BeApproximately(100f, 0.5f, "SW corner pinned");
        mesh.Vertices[15].Z.Should().BeApproximately(100f, 0.5f, "SE corner pinned");
        mesh.Vertices[5].Z.Should().BeApproximately(200f, 0.5f, "interior keeps the detail height");
        mesh.Vertices[10].Z.Should().BeApproximately(200f, 0.5f, "interior keeps the detail height");
    }

    [Fact]
    public void BuildTiles_EdgeMatchRows_MorphsTheOuterBandFromBaseToDetail()
    {
        DemRaster baseLayer = Flat(7, 100.0);
        DemRaster detail = Flat(7, 200.0);
        var options = new TerrainMeshOptions { VerticalExaggeration = 1f };

        TerrainMesh3D mesh = TerrainMesh3D.BuildTiles(detail, options, edgeHeightSource: baseLayer, edgeMatchRows: 3)[0];

        // 7×7 row-major (tileCols=7). Distance from the nearest edge: (0,0)=0, (1,1)=1, (2,2)=2, (3,3)=3.
        mesh.Vertices[0].Z.Should().BeApproximately(100f, 0.5f, "the very edge = full base");
        mesh.Vertices[(1 * 7) + 1].Z.Should().BeApproximately(133.33f, 0.5f, "dist 1 of a 3-wide band = 2/3 base + 1/3 detail");
        mesh.Vertices[(2 * 7) + 2].Z.Should().BeApproximately(166.67f, 0.5f, "dist 2 = 1/3 base + 2/3 detail");
        mesh.Vertices[(3 * 7) + 3].Z.Should().BeApproximately(200f, 0.5f, "centre (dist 3, outside the band) = full detail");
    }

    [Fact]
    public void BuildTiles_WithoutEdgeHeightSource_KeepsDetailHeightsEverywhere()
    {
        DemRaster detail = Flat(4, 200.0);

        TerrainMesh3D mesh = TerrainMesh3D.BuildTiles(detail, new TerrainMeshOptions { VerticalExaggeration = 1f })[0];

        mesh.Vertices[0].Z.Should().BeApproximately(200f, 0.5f, "no edge source → perimeter stays detail height");
        mesh.Vertices[5].Z.Should().BeApproximately(200f, 0.5f);
    }

    [Fact]
    public void BuildTiles_EdgeHeightSource_IgnoresNoDataAndKeepsDetail()
    {
        // A base that is entirely no-data must not drag the perimeter to the sentinel — keep the detail.
        var noData = new float[4 * 4];
        Array.Fill(noData, -9999f);
        var baseLayer = new DemRaster(4, 4, Bounds, noData, noDataValue: -9999f);
        DemRaster detail = Flat(4, 200.0);

        TerrainMesh3D mesh = TerrainMesh3D.BuildTiles(detail, new TerrainMeshOptions { VerticalExaggeration = 1f }, edgeHeightSource: baseLayer)[0];

        mesh.Vertices[0].Z.Should().BeApproximately(200f, 0.5f, "no-data base leaves the perimeter at detail height");
    }
}