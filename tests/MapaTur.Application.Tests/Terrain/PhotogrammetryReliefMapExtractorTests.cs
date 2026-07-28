using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class PhotogrammetryReliefMapExtractorTests
{
    [Fact]
    public void should_rasterize_a_real_mesh_bulge_as_relief_instead_of_inventing_noise()
    {
        // Arrange
        PhotogrammetryRockPrimitive primitive = CreateRaisedGrid();

        // Act
        RockHeightMap result = PhotogrammetryReliefMapExtractor.Extract(primitive, 33, 33);

        // Assert
        result.SampleWrapped(0.5f, 0.5f).Should()
            .BeGreaterThan(result.SampleWrapped(0.08f, 0.08f) + 0.35f);
    }

    [Fact]
    public void should_keep_the_frontmost_surface_when_the_scan_contains_overhangs()
    {
        // Arrange
        PhotogrammetryRockPrimitive primitive = CreateOverlappingFaces();

        // Act
        RockHeightMap result = PhotogrammetryReliefMapExtractor.Extract(primitive, 17, 17);

        // Assert
        result.SampleWrapped(0.5f, 0.5f).Should().BeGreaterThan(0.8f);
    }

    private static PhotogrammetryRockPrimitive CreateRaisedGrid()
    {
        Vector3[] positions =
        [
            new(-1f, -1f, 0f),
            new(0f, -1f, 0.1f),
            new(1f, -1f, 0f),
            new(-1f, 0f, 0.1f),
            new(0f, 0f, 2f),
            new(1f, 0f, 0.1f),
            new(-1f, 1f, 0f),
            new(0f, 1f, 0.1f),
            new(1f, 1f, 0f),
        ];
        uint[] indices =
        [
            0, 1, 3, 1, 4, 3,
            1, 2, 4, 2, 5, 4,
            3, 4, 6, 4, 7, 6,
            4, 5, 7, 5, 8, 7,
        ];
        return Primitive(positions, indices);
    }

    private static PhotogrammetryRockPrimitive CreateOverlappingFaces()
    {
        Vector3[] positions =
        [
            new(-1f, -1f, 0f),
            new(1f, -1f, 0f),
            new(0f, 1f, 0f),
            new(-0.8f, -0.8f, 3f),
            new(0.8f, -0.8f, 3f),
            new(0f, 0.8f, 3f),
        ];
        return Primitive(positions, [0, 1, 2, 3, 4, 5]);
    }

    private static PhotogrammetryRockPrimitive Primitive(Vector3[] positions, uint[] indices) =>
        new(
            positions,
            Enumerable.Repeat(Vector3.UnitZ, positions.Length).ToArray(),
            Enumerable.Repeat(Vector2.Zero, positions.Length).ToArray(),
            indices,
            baseColorImageBytes: null);
}
