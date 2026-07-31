using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class PhotogrammetryRockRegionClipperTests
{
    [Fact]
    public void should_keep_only_geometry_inside_the_assigned_wall_region()
    {
        // Arrange
        PhotogrammetryRockPrimitive source = CreateCrossingQuad();
        var region = new RockWallClipRegion(
            Vector3.Zero,
            Vector3.UnitY,
            WidthMeters: 2f,
            HeightMeters: 2f);

        // Act
        PhotogrammetryRockPrimitive clipped =
            PhotogrammetryRockRegionClipper.Clip(source, region);

        // Assert
        clipped.Positions
            .Where((_, index) => clipped.Indices.Contains((uint)index))
            .Should()
            .OnlyContain(position =>
                position.X >= -1.0001f
                && position.X <= 1.0001f
                && position.Z >= -1.0001f
                && position.Z <= 1.0001f);
    }

    [Fact]
    public void should_interpolate_uvs_on_a_clipped_original_triangle()
    {
        // Arrange
        PhotogrammetryRockPrimitive source = CreateCrossingQuad();
        var region = new RockWallClipRegion(
            Vector3.Zero,
            Vector3.UnitY,
            WidthMeters: 2f,
            HeightMeters: 2f);

        // Act
        PhotogrammetryRockPrimitive clipped =
            PhotogrammetryRockRegionClipper.Clip(source, region);

        // Assert
        clipped.TexCoords.Should().Contain(uv =>
            uv.X > 0f && uv.X < 1f && uv.Y > 0f && uv.Y < 1f);
    }

    [Fact]
    public void should_preserve_full_3d_relief_while_clipping_in_the_wall_plane()
    {
        // Arrange
        PhotogrammetryRockPrimitive source = CreateCrossingQuad();
        var region = new RockWallClipRegion(
            Vector3.Zero,
            Vector3.UnitY,
            WidthMeters: 2f,
            HeightMeters: 2f);

        // Act
        PhotogrammetryRockPrimitive clipped =
            PhotogrammetryRockRegionClipper.Clip(source, region);

        // Assert
        clipped.Positions
            .Where((_, index) => clipped.Indices.Contains((uint)index))
            .Max(position => position.Y)
            .Should()
            .BeGreaterThan(0.5f);
    }

    private static PhotogrammetryRockPrimitive CreateCrossingQuad() => new(
        positions:
        [
            new(-2f, 0.2f, -2f),
            new(2f, 0.6f, -2f),
            new(2f, 1.1f, 2f),
            new(-2f, 0.4f, 2f),
        ],
        normals: Enumerable.Repeat(Vector3.UnitY, 4).ToArray(),
        texCoords: [Vector2.Zero, Vector2.UnitX, Vector2.One, Vector2.UnitY],
        indices: [0u, 1u, 2u, 0u, 2u, 3u],
        baseColorImageBytes: null);
}
