using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;

namespace MapaTur.Application.Tests.Terrain;

public sealed class RockDemTileClassifierTests
{
    [Fact]
    public void should_reject_flat_dem_tile()
    {
        // Arrange
        BakedDemTile tile = CreateTile((_, _) => 1500f);

        // Act
        RockDemTileEvidence evidence = RockDemTileClassifier.Analyze(tile, sampleStride: 1);

        // Assert
        evidence.IsCandidate.Should().BeFalse();
    }

    [Fact]
    public void should_accept_coherent_steep_dem_surface()
    {
        // Arrange
        BakedDemTile tile = CreateTile((column, _) => 1400f + (column * 12f));

        // Act
        RockDemTileEvidence evidence = RockDemTileClassifier.Analyze(tile, sampleStride: 1);

        // Assert
        evidence.IsCandidate.Should().BeTrue();
    }

    [Fact]
    public void should_reject_single_height_spike()
    {
        // Arrange
        BakedDemTile tile = CreateTile(
            (column, row) => column == 5 && row == 5 ? 2200f : 1500f);

        // Act
        RockDemTileEvidence evidence = RockDemTileClassifier.Analyze(tile, sampleStride: 1);

        // Assert
        evidence.IsCandidate.Should().BeFalse();
    }

    [Fact]
    public void should_reject_tile_without_valid_height_neighbourhoods()
    {
        // Arrange
        BakedDemTile tile = CreateTile((_, _) => -9999f);

        // Act
        RockDemTileEvidence evidence = RockDemTileClassifier.Analyze(tile, sampleStride: 1);

        // Assert
        evidence.ValidSampleCount.Should().Be(0);
    }

    private static BakedDemTile CreateTile(Func<int, int, float> height)
    {
        const int size = 11;
        var heights = new float[size * size];
        for (int row = 0; row < size; row++)
        {
            for (int column = 0; column < size; column++)
            {
                heights[(row * size) + column] = height(column, row);
            }
        }

        var bounds = new MapBounds(
            new GeoPoint(49.2, 20.0),
            new GeoPoint(49.201, 20.001));
        return new BakedDemTile(
            zoom: 17,
            tileX: 72838,
            tileY: 44908,
            columns: size,
            rows: size,
            bounds,
            noDataValue: -9999,
            heights);
    }
}
