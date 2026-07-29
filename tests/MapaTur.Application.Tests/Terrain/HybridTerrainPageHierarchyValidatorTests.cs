using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class HybridTerrainPageHierarchyValidatorTests
{
    [Fact]
    public void should_accept_complete_monotonic_parent_chain()
    {
        // Arrange
        HybridTerrainPageDescriptor[] pages =
        [
            Descriptor(lod: 0, x: 1, y: 1, error: 0.01f),
            Descriptor(lod: 1, x: 0, y: 0, error: 0.35f),
            Descriptor(lod: 2, x: 0, y: 0, error: 1.2f),
        ];

        // Act
        Action validate = () => HybridTerrainPageHierarchyValidator.Validate(pages);

        // Assert
        validate.Should().NotThrow();
    }

    [Fact]
    public void should_reject_child_without_resident_fallback_parent()
    {
        // Arrange
        HybridTerrainPageDescriptor[] pages =
        [
            Descriptor(lod: 0, x: 1, y: 1, error: 0.01f),
            Descriptor(lod: 2, x: 0, y: 0, error: 1.2f),
        ];

        // Act
        Action validate = () => HybridTerrainPageHierarchyValidator.Validate(pages);

        // Assert
        validate.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void should_reject_parent_with_smaller_geometric_error_than_child()
    {
        // Arrange
        HybridTerrainPageDescriptor[] pages =
        [
            Descriptor(lod: 0, x: 1, y: 1, error: 0.4f),
            Descriptor(lod: 1, x: 0, y: 0, error: 0.35f),
            Descriptor(lod: 2, x: 0, y: 0, error: 1.2f),
        ];

        // Act
        Action validate = () => HybridTerrainPageHierarchyValidator.Validate(pages);

        // Assert
        validate.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void should_resolve_negative_parent_coordinates_with_floor_division()
    {
        // Arrange
        HybridTerrainPageDescriptor[] pages =
        [
            Descriptor(lod: 0, x: -1, y: -1, error: 0.01f),
            Descriptor(lod: 1, x: -1, y: -1, error: 0.35f),
            Descriptor(lod: 2, x: -1, y: -1, error: 1.2f),
        ];

        // Act
        Action validate = () => HybridTerrainPageHierarchyValidator.Validate(pages);

        // Assert
        validate.Should().NotThrow();
    }

    private static HybridTerrainPageDescriptor Descriptor(byte lod, int x, int y, float error)
    {
        float size = 32f * (1 << lod);
        return new HybridTerrainPageDescriptor(
            new HybridTerrainPageKey(x, y, lod),
            new Vector3(x * size, y * size, 1800f),
            new Vector3(size, size, 50f),
            error,
            vertexCount: 3,
            indexCount: 3,
            path: $"{x}-{y}-{lod}.rmp3");
    }
}
