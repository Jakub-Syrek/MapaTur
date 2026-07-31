using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class SteepRockRegionMergerTests
{
    [Fact]
    public void should_merge_touching_blocks_that_face_the_same_direction()
    {
        // Arrange
        SteepRockRegion[] blocks =
        [
            new(new Vector3(-10f, 0f, 20f), Vector3.UnitY, 24f, 30f, 100),
            new(new Vector3(12f, 1f, 24f), Vector3.Normalize(new Vector3(0.1f, 1f, 0f)), 26f, 34f, 120),
        ];

        // Act
        IReadOnlyList<SteepRockRegion> merged = SteepRockRegionMerger.Merge(
            blocks,
            maximumGapMeters: 8f,
            maximumNormalAngleDegrees: 25f);

        // Assert
        merged.Should().ContainSingle();
    }

    [Fact]
    public void should_keep_opposing_rock_faces_separate()
    {
        // Arrange
        SteepRockRegion[] blocks =
        [
            new(new Vector3(-4f, 0f, 20f), Vector3.UnitY, 24f, 30f, 100),
            new(new Vector3(4f, 0f, 20f), -Vector3.UnitY, 24f, 30f, 100),
        ];

        // Act
        IReadOnlyList<SteepRockRegion> merged = SteepRockRegionMerger.Merge(
            blocks,
            maximumGapMeters: 8f,
            maximumNormalAngleDegrees: 25f);

        // Assert
        merged.Should().HaveCount(2);
    }

    [Fact]
    public void should_expand_the_merged_facet_over_all_source_block_extents()
    {
        // Arrange
        SteepRockRegion[] blocks =
        [
            new(new Vector3(-12f, 0f, 10f), Vector3.UnitY, 20f, 20f, 80),
            new(new Vector3(12f, 0f, 30f), Vector3.UnitY, 20f, 24f, 90),
        ];

        // Act
        SteepRockRegion merged = SteepRockRegionMerger.Merge(
            blocks,
            maximumGapMeters: 8f,
            maximumNormalAngleDegrees: 25f).Single();

        // Assert
        (merged.WidthMeters, merged.HeightMeters)
            .Should()
            .Match<(float Width, float Height)>(size => size.Width >= 44f && size.Height >= 42f);
    }
}
