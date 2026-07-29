using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class RockDemCoveragePlannerTests
{
    [Fact]
    public void should_add_one_available_tile_halo_around_rock_candidate()
    {
        // Arrange
        DemTileKey candidate = new(17, 100, 200);
        DemTileKey[] available =
        [
            .. from x in Enumerable.Range(99, 3)
            from y in Enumerable.Range(199, 3)
            select new DemTileKey(17, x, y),
            new DemTileKey(17, 110, 210),
        ];

        // Act
        IReadOnlySet<DemTileKey> result = RockDemCoveragePlanner.ExpandWithHalo(
            [candidate],
            available,
            haloTiles: 1);

        // Assert
        result.Should().HaveCount(9);
    }

    [Fact]
    public void should_not_plan_halo_tile_when_dem_is_missing()
    {
        // Arrange
        DemTileKey candidate = new(17, 100, 200);
        DemTileKey missingNeighbour = new(17, 101, 200);
        DemTileKey[] available =
        [
            candidate,
            new DemTileKey(17, 99, 200),
        ];

        // Act
        IReadOnlySet<DemTileKey> result = RockDemCoveragePlanner.ExpandWithHalo(
            [candidate],
            available,
            haloTiles: 1);

        // Assert
        result.Should().NotContain(missingNeighbour);
    }

    [Fact]
    public void should_keep_only_candidates_when_halo_is_zero()
    {
        // Arrange
        DemTileKey candidate = new(17, 100, 200);
        DemTileKey neighbour = new(17, 101, 200);

        // Act
        IReadOnlySet<DemTileKey> result = RockDemCoveragePlanner.ExpandWithHalo(
            [candidate],
            [candidate, neighbour],
            haloTiles: 0);

        // Assert
        result.Should().Equal(candidate);
    }
}
