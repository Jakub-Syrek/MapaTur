using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour pinning for <see cref="DemTileMosaic.Stitch"/>, which assembles a grid of per-tile
/// <see cref="DemRaster"/>s (as returned by an <see cref="IDemTileSource"/> for a region) into one
/// raster the mesh builder can consume: tiles placed by their X/Y, north-up, gaps filled with NoData.
/// </summary>
public sealed class DemTileMosaicTests
{
    private static PlacedDemTile Tile(int zoom, int x, int y, int w, int h, float fill)
    {
        var (west, south, east, north) = SlippyTileMath.TileBounds(x, y, zoom);
        var bounds = new MapBounds(new GeoPoint(south, west), new GeoPoint(north, east));
        var samples = new float[w * h];
        Array.Fill(samples, fill);
        return new PlacedDemTile(new DemTileKey(zoom, x, y), new DemRaster(w, h, bounds, samples));
    }

    [Fact]
    public void Stitch_SingleTile_PreservesDimensionsAndSamples()
    {
        var tile = Tile(10, 100, 100, 2, 2, 5f);

        DemRaster mosaic = DemTileMosaic.Stitch(new[] { tile });

        mosaic.Columns.Should().Be(2);
        mosaic.Rows.Should().Be(2);
        mosaic.Samples.Should().OnlyContain(v => v == 5f);
    }

    [Fact]
    public void Stitch_TwoTilesAcrossX_PlacesLowerXOnTheWest()
    {
        var westTile = Tile(10, 100, 100, 2, 2, 1f);
        var eastTile = Tile(10, 101, 100, 2, 2, 2f);

        DemRaster mosaic = DemTileMosaic.Stitch(new[] { eastTile, westTile });

        mosaic.Columns.Should().Be(4);
        mosaic.Rows.Should().Be(2);
        mosaic[0, 0].Should().Be(1f, "the lower-X tile is on the west (left)");
        mosaic[3, 0].Should().Be(2f, "the higher-X tile is on the east (right)");
    }

    [Fact]
    public void Stitch_TwoTilesAcrossY_PlacesLowerYOnTheNorth()
    {
        var northTile = Tile(10, 100, 100, 2, 2, 7f);
        var southTile = Tile(10, 100, 101, 2, 2, 8f);

        DemRaster mosaic = DemTileMosaic.Stitch(new[] { southTile, northTile });

        mosaic.Columns.Should().Be(2);
        mosaic.Rows.Should().Be(4);
        mosaic[0, 0].Should().Be(7f, "the lower-Y tile is on the north (top rows)");
        mosaic[0, 3].Should().Be(8f, "the higher-Y tile is on the south (bottom rows)");
    }

    [Fact]
    public void Stitch_TwoByTwo_PlacesEveryQuadrant()
    {
        var topLeft = Tile(10, 100, 100, 2, 2, 1f);
        var topRight = Tile(10, 101, 100, 2, 2, 2f);
        var bottomLeft = Tile(10, 100, 101, 2, 2, 3f);
        var bottomRight = Tile(10, 101, 101, 2, 2, 4f);

        DemRaster mosaic = DemTileMosaic.Stitch(new[] { topLeft, topRight, bottomLeft, bottomRight });

        mosaic.Columns.Should().Be(4);
        mosaic.Rows.Should().Be(4);
        mosaic[0, 0].Should().Be(1f);
        mosaic[3, 0].Should().Be(2f);
        mosaic[0, 3].Should().Be(3f);
        mosaic[3, 3].Should().Be(4f);
    }

    [Fact]
    public void Stitch_CombinedBoundsSpanAllTiles()
    {
        var topLeft = Tile(10, 100, 100, 2, 2, 1f);
        var bottomRight = Tile(10, 101, 101, 2, 2, 4f);

        DemRaster mosaic = DemTileMosaic.Stitch(new[] { topLeft, bottomRight });

        var (west, _, _, north) = SlippyTileMath.TileBounds(100, 100, 10);
        var (_, south, east, _) = SlippyTileMath.TileBounds(101, 101, 10);
        mosaic.West.Should().BeApproximately(west, 1e-9);
        mosaic.North.Should().BeApproximately(north, 1e-9);
        mosaic.East.Should().BeApproximately(east, 1e-9);
        mosaic.South.Should().BeApproximately(south, 1e-9);
    }

    [Fact]
    public void Stitch_MissingCell_IsFilledWithNoData()
    {
        // Provide 3 of the 4 quadrants — the missing bottom-right block must read as NoData.
        var topLeft = Tile(10, 100, 100, 2, 2, 1f);
        var topRight = Tile(10, 101, 100, 2, 2, 2f);
        var bottomLeft = Tile(10, 100, 101, 2, 2, 3f);

        DemRaster mosaic = DemTileMosaic.Stitch(new[] { topLeft, topRight, bottomLeft });

        mosaic[3, 3].Should().Be(mosaic.NoDataValue);
    }

    [Fact]
    public void Stitch_RejectsEmpty()
    {
        var act = () => DemTileMosaic.Stitch(Array.Empty<PlacedDemTile>());

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Stitch_RejectsMixedTileDimensions()
    {
        var a = Tile(10, 100, 100, 2, 2, 1f);
        var b = Tile(10, 101, 100, 3, 3, 2f);

        var act = () => DemTileMosaic.Stitch(new[] { a, b });

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Stitch_RejectsMixedZoom()
    {
        var a = Tile(10, 100, 100, 2, 2, 1f);
        var b = Tile(11, 101, 100, 2, 2, 2f);

        var act = () => DemTileMosaic.Stitch(new[] { a, b });

        act.Should().Throw<ArgumentException>();
    }
}