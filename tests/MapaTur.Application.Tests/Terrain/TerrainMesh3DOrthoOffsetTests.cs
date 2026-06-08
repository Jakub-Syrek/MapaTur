using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class TerrainMesh3DOrthoOffsetTests
{
    private static readonly MapBounds Bounds = new(new GeoPoint(49.0, 20.0), new GeoPoint(49.1, 20.1));

    private static DemRaster Raster()
    {
        const int n = 6;
        var samples = new float[n * n];
        Array.Fill(samples, 1000f);
        return new DemRaster(n, n, Bounds, samples);
    }

    [Fact]
    public void BuildTiles_OffsetsEveryTilesOrthoTileIndex()
    {
        var raster = Raster();
        var coverage = new OrthoCoverage(Bounds, GridCols: 2, GridRows: 2);

        var baseline = TerrainMesh3D.BuildTiles(raster, maxTileSide: 2, orthoCoverage: coverage);
        var offset = TerrainMesh3D.BuildTiles(raster, maxTileSide: 2, orthoCoverage: coverage, orthoTileIndexOffset: 100);

        offset.Should().HaveSameCount(baseline);
        for (int i = 0; i < baseline.Count; i++)
        {
            offset[i].OrthoTileIndex.Should().Be(baseline[i].OrthoTileIndex + 100);
        }
    }

    [Fact]
    public void BuildTiles_DefaultOffsetIsZero()
    {
        var raster = Raster();
        var coverage = new OrthoCoverage(Bounds, GridCols: 2, GridRows: 2);

        var withDefault = TerrainMesh3D.BuildTiles(raster, maxTileSide: 2, orthoCoverage: coverage);
        var withExplicitZero = TerrainMesh3D.BuildTiles(raster, maxTileSide: 2, orthoCoverage: coverage, orthoTileIndexOffset: 0);

        for (int i = 0; i < withDefault.Count; i++)
        {
            withExplicitZero[i].OrthoTileIndex.Should().Be(withDefault[i].OrthoTileIndex);
        }
    }
}