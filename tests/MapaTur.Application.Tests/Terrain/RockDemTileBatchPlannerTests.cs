using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class RockDemTileBatchPlannerTests
{
    [Fact]
    public void should_split_rows_gaps_and_maximum_batch_size()
    {
        // Arrange
        DemTileKey[] keys =
        [
            new(17, 10, 20),
            new(17, 11, 20),
            new(17, 12, 20),
            new(17, 13, 20),
            new(17, 18, 20),
            new(17, 10, 21),
        ];

        // Act
        IReadOnlyList<IReadOnlyList<DemTileKey>> batches =
            RockDemTileBatchPlanner.CreateContiguousRowBatches(keys, maximumTilesPerBatch: 3);

        // Assert
        batches.Select(batch => batch.Count).Should().Equal(3, 1, 1, 1);
    }

    [Fact]
    public void should_keep_each_batch_in_deterministic_x_order()
    {
        // Arrange
        DemTileKey[] keys =
        [
            new(17, 12, 20),
            new(17, 10, 20),
            new(17, 11, 20),
        ];

        // Act
        IReadOnlyList<IReadOnlyList<DemTileKey>> batches =
            RockDemTileBatchPlanner.CreateContiguousRowBatches(keys, maximumTilesPerBatch: 6);

        // Assert
        batches.Single().Select(key => key.X).Should().Equal(10, 11, 12);
    }
}
