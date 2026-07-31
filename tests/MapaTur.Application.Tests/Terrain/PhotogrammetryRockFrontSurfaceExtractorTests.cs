using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class PhotogrammetryRockFrontSurfaceExtractorTests
{
    [Fact]
    public void should_remove_hidden_rear_layer_without_resampling_front_geometry()
    {
        // Arrange
        PhotogrammetryRockPrimitive source = CreateLayeredPrimitive();

        // Act
        PhotogrammetryRockPrimitive front =
            PhotogrammetryRockFrontSurfaceExtractor.Extract(source, Vector3.UnitZ);

        // Assert
        front.Indices.Should().Equal(4u, 5u, 6u, 4u, 6u, 7u);
    }

    [Fact]
    public void should_remove_technical_side_faces_parallel_to_view_direction()
    {
        // Arrange
        PhotogrammetryRockPrimitive source = CreateLayeredPrimitive();

        // Act
        PhotogrammetryRockPrimitive front =
            PhotogrammetryRockFrontSurfaceExtractor.Extract(source, Vector3.UnitZ);

        // Assert
        bool containsTechnicalSide = front.Indices
            .Chunk(3)
            .Any(triangle =>
                triangle.SequenceEqual(new uint[] { 0u, 1u, 5u })
                || triangle.SequenceEqual(new uint[] { 0u, 5u, 4u }));
        containsTechnicalSide.Should().BeFalse();
    }

    [Fact]
    public void should_preserve_original_vertices_uvs_and_irregular_topology()
    {
        // Arrange
        PhotogrammetryRockPrimitive source = CreateLayeredPrimitive();

        // Act
        PhotogrammetryRockPrimitive front =
            PhotogrammetryRockFrontSurfaceExtractor.Extract(source, Vector3.UnitZ);

        // Assert
        front.Positions.Should().BeSameAs(source.Positions);
        front.TexCoords.Should().BeSameAs(source.TexCoords);
    }

    [Fact]
    public void should_remove_oblique_capture_skirt_that_only_grazes_the_scan_front()
    {
        // Arrange
        var source = new PhotogrammetryRockPrimitive(
            positions:
            [
                new(0f, 0f, 0f),
                new(1f, 0f, 0f),
                new(0f, 1f, 0f),
                new(2f, 0f, 0f),
                new(2.1f, 0f, 1f),
                new(2f, 1f, 0f),
            ],
            normals: Enumerable.Repeat(Vector3.UnitZ, 6).ToArray(),
            texCoords: Enumerable.Repeat(Vector2.Zero, 6).ToArray(),
            indices: [0u, 1u, 2u, 3u, 4u, 5u],
            baseColorImageBytes: null);

        // Act
        PhotogrammetryRockPrimitive front =
            PhotogrammetryRockFrontSurfaceExtractor.Extract(source, Vector3.UnitZ);

        // Assert
        front.Indices.Should().Equal(0u, 1u, 2u);
    }

    [Fact]
    public void should_remove_long_skinny_photogrammetry_hole_stitch()
    {
        // Arrange
        var source = new PhotogrammetryRockPrimitive(
            positions:
            [
                new(0f, 0f, 0f),
                new(1f, 0f, 0f),
                new(0.2f, 0.8f, 0f),
                new(2f, 0f, 0f),
                new(22f, 0f, 0f),
                new(22f, 0.08f, 0f),
            ],
            normals: Enumerable.Repeat(Vector3.UnitZ, 6).ToArray(),
            texCoords: Enumerable.Repeat(Vector2.Zero, 6).ToArray(),
            indices: [0u, 1u, 2u, 3u, 4u, 5u],
            baseColorImageBytes: null);

        // Act
        PhotogrammetryRockPrimitive front =
            PhotogrammetryRockFrontSurfaceExtractor.Extract(source, Vector3.UnitZ);

        // Assert
        front.Indices.Should().Equal(0u, 1u, 2u);
    }

    private static PhotogrammetryRockPrimitive CreateLayeredPrimitive()
    {
        Vector3[] positions =
        [
            new(-2f, -1f, 0f),
            new(2f, -1f, 0f),
            new(1.4f, 1.7f, 0f),
            new(-1.8f, 1.2f, 0f),
            new(-2f, -1f, 1f),
            new(2f, -1f, 1.3f),
            new(1.4f, 1.7f, 1.8f),
            new(-1.8f, 1.2f, 0.9f),
        ];
        Vector3[] normals =
        [
            -Vector3.UnitZ,
            -Vector3.UnitZ,
            -Vector3.UnitZ,
            -Vector3.UnitZ,
            Vector3.UnitZ,
            Vector3.UnitZ,
            Vector3.UnitZ,
            Vector3.UnitZ,
        ];
        Vector2[] uvs =
        [
            new(0.02f, 0.04f),
            new(0.91f, 0.08f),
            new(0.82f, 0.94f),
            new(0.07f, 0.79f),
            new(0.13f, 0.16f),
            new(0.86f, 0.11f),
            new(0.77f, 0.89f),
            new(0.19f, 0.74f),
        ];

        return new PhotogrammetryRockPrimitive(
            positions,
            normals,
            uvs,
            indices:
            [
                0u, 2u, 1u,
                0u, 3u, 2u,
                4u, 5u, 6u,
                4u, 6u, 7u,
                0u, 1u, 5u,
                0u, 5u, 4u,
            ],
            baseColorImageBytes: null);
    }
}
