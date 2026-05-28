using FluentAssertions;

using MapaTur.Domain.Maps;

namespace MapaTur.Domain.Tests.Maps;

public sealed class TileCoordinateTests
{
    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(1, 0, 0, 1)]
    [InlineData(1, 0, 1, 0)]
    [InlineData(10, 550, 350, 673)]
    public void ToTmsRow_FlipsRowWithinZoomLevel(int zoom, int x, int xyzRow, int expectedTmsRow)
    {
        var coordinate = new TileCoordinate(zoom, x, xyzRow);

        coordinate.ToTmsRow().Should().Be(expectedTmsRow);
    }

    [Fact]
    public void FromTms_IsInverseOfToTmsRow()
    {
        var original = new TileCoordinate(12, 2200, 1400);

        int tmsRow = original.ToTmsRow();
        var roundTripped = TileCoordinate.FromTms(original.ZoomLevel, original.Column, tmsRow);

        roundTripped.Should().Be(original);
    }
}