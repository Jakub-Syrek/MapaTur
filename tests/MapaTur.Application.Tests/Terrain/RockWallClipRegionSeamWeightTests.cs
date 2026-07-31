using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class RockWallClipRegionSeamWeightTests
{
    [Fact]
    public void should_make_every_geometric_clip_edge_fully_transparent()
    {
        // Arrange
        PhotogrammetryRockPrimitive primitive = CreateStrip();
        var region = new RockWallClipRegion(
            Vector3.Zero,
            Vector3.UnitY,
            WidthMeters: 2f,
            HeightMeters: 2f,
            Seed: 1234);

        // Act
        byte[] weights = RockWallSurfaceConformer.CalculateClipRegionSeamWeights(
            primitive,
            region,
            edgeBlendFraction: 0.2f);

        // Assert
        weights[0].Should().Be(byte.MinValue);
    }

    [Fact]
    public void should_vary_the_inward_fade_without_a_repeating_boundary_stamp()
    {
        // Arrange
        PhotogrammetryRockPrimitive primitive = CreateStrip();
        var region = new RockWallClipRegion(
            Vector3.Zero,
            Vector3.UnitY,
            WidthMeters: 2f,
            HeightMeters: 2f,
            Seed: 5678);

        // Act
        byte[] weights = RockWallSurfaceConformer.CalculateClipRegionSeamWeights(
            primitive,
            region,
            edgeBlendFraction: 0.35f);

        // Assert
        weights.Skip(1).Distinct().Should().HaveCountGreaterThan(1);
    }

    [Fact]
    public void should_return_identical_irregular_fade_for_the_same_seed()
    {
        // Arrange
        PhotogrammetryRockPrimitive primitive = CreateStrip();
        var region = new RockWallClipRegion(
            Vector3.Zero,
            Vector3.UnitY,
            WidthMeters: 2f,
            HeightMeters: 2f,
            Seed: 9012);

        // Act
        byte[] first = RockWallSurfaceConformer.CalculateClipRegionSeamWeights(
            primitive,
            region,
            edgeBlendFraction: 0.35f);
        byte[] second = RockWallSurfaceConformer.CalculateClipRegionSeamWeights(
            primitive,
            region,
            edgeBlendFraction: 0.35f);

        // Assert
        second.Should().Equal(first);
    }

    private static PhotogrammetryRockPrimitive CreateStrip() => new(
        positions:
        [
            new(-1f, 0.4f, -1f),
            new(-0.45f, 0.6f, -0.75f),
            new(-0.45f, 0.8f, -0.25f),
            new(-0.45f, 0.9f, 0.25f),
            new(-0.45f, 0.7f, 0.75f),
            new(1f, 0.5f, 1f),
        ],
        normals: Enumerable.Repeat(Vector3.UnitY, 6).ToArray(),
        texCoords: Enumerable.Repeat(Vector2.Zero, 6).ToArray(),
        indices:
        [
            0u, 1u, 2u,
            0u, 2u, 3u,
            0u, 3u, 4u,
            0u, 4u, 5u,
        ],
        baseColorImageBytes: null);
}
