using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;

namespace MapaTur.Application.Tests.Terrain;

public sealed class RockDemRegionAssemblerTests
{
    [Fact]
    public void should_keep_the_shared_edge_of_adjacent_dem_tiles_inside_the_rock_surface()
    {
        // Arrange
        BakedDemTile west = CreateWallTile(
            tileX: 10,
            west: 19.9998,
            east: 20.0,
            baseHeight: 1200f);
        BakedDemTile east = CreateWallTile(
            tileX: 11,
            west: 20.0,
            east: 20.0002,
            baseHeight: 1280f);
        var anchor = new GeoPoint(49.25, 20.0);

        // Act
        IReadOnlyList<RockMeshTriangle> source =
            RockDemRegionAssembler.Assemble([west, east], anchor);
        PhotogrammetryRockPrimitive result = ContinuousScannedRockSurfaceBuilder.Build(
            source,
            static (_, _) => new RockSurfaceSample(0f, 255, 0),
            sampleAmplitudeMeters: 1f,
            maximumReliefMeters: 1f,
            maximumEdgeMeters: 20f,
            seed: 20260728,
            baseColorImageBytes: null);

        // Assert
        result.Positions
            .Select((position, index) => (position, weight: result.SeamWeights[index]))
            .Where(vertex => MathF.Abs(vertex.position.X) < 0.5f)
            .Should()
            .Contain(vertex => vertex.weight > 240);
    }

    [Fact]
    public void should_reject_overlapping_lod_tiles_in_one_region()
    {
        // Arrange
        BakedDemTile first = CreateWallTile(tileX: 10, west: 19.9998, east: 20.0, baseHeight: 1200f);
        BakedDemTile duplicate = CreateWallTile(tileX: 10, west: 19.9998, east: 20.0, baseHeight: 1200f);
        var anchor = new GeoPoint(49.25, 20.0);

        // Act
        Action act = () => RockDemRegionAssembler.Assemble([first, duplicate], anchor);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    private static BakedDemTile CreateWallTile(int tileX, double west, double east, float baseHeight)
    {
        const int columns = 3;
        const int rows = 5;
        var heights = new float[columns * rows];
        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                heights[(row * columns) + column] = baseHeight + (column * 40f);
            }
        }

        return new BakedDemTile(
            zoom: 17,
            tileX,
            tileY: 20,
            columns,
            rows,
            new MapBounds(
                new GeoPoint(49.2498, west),
                new GeoPoint(49.2502, east)),
            noDataValue: -9999,
            heights);
    }
}
