using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class SteepRockFacetSplitterTests
{
    [Fact]
    public void should_split_a_large_facet_below_the_scan_scale_limit()
    {
        // Arrange
        var facet = new SteepRockRegion(Vector3.Zero, Vector3.UnitY, 210f, 150f, 1200);

        // Act
        IReadOnlyList<SteepRockRegion> pieces = SteepRockFacetSplitter.Split(
            [facet],
            maximumWidthMeters: 70f,
            maximumHeightMeters: 70f,
            seed: 271828);

        // Assert
        pieces.Should().OnlyContain(piece => piece.WidthMeters <= 70f && piece.HeightMeters <= 70f);
    }

    [Fact]
    public void should_preserve_the_complete_facet_area_without_grid_overlap()
    {
        // Arrange
        var facet = new SteepRockRegion(new Vector3(10f, 20f, 30f), Vector3.UnitY, 210f, 150f, 1200);

        // Act
        IReadOnlyList<SteepRockRegion> pieces = SteepRockFacetSplitter.Split(
            [facet],
            maximumWidthMeters: 70f,
            maximumHeightMeters: 70f,
            seed: 271828);

        // Assert
        pieces.Sum(piece => piece.WidthMeters * piece.HeightMeters)
            .Should()
            .BeApproximately(facet.WidthMeters * facet.HeightMeters, 0.1f);
    }

    [Fact]
    public void should_change_irregular_cut_positions_when_the_seed_changes()
    {
        // Arrange
        var facet = new SteepRockRegion(Vector3.Zero, Vector3.UnitY, 210f, 150f, 1200);

        // Act
        IReadOnlyList<SteepRockRegion> first = SteepRockFacetSplitter.Split([facet], 70f, 70f, seed: 1);
        IReadOnlyList<SteepRockRegion> second = SteepRockFacetSplitter.Split([facet], 70f, 70f, seed: 2);

        // Assert
        second.Should().NotEqual(first);
    }
}
