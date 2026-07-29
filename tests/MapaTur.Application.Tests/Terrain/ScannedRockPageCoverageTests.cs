using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class ScannedRockPageCoverageTests
{
    [Fact]
    public void should_reject_page_without_visible_rock_weight()
    {
        // Arrange
        ScannedRockMeshPage page = BakeTriangle([0, 0, 0]);

        // Act
        bool result = ScannedRockPageCoverage.HasVisibleRock(page);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void should_keep_page_containing_visible_rock_weight()
    {
        // Arrange
        ScannedRockMeshPage page = BakeTriangle([255, 180, 0]);

        // Act
        bool result = ScannedRockPageCoverage.HasVisibleRock(page);

        // Assert
        result.Should().BeTrue();
    }

    private static ScannedRockMeshPage BakeTriangle(byte[] seamWeights)
    {
        var primitive = new PhotogrammetryRockPrimitive(
            positions:
            [
                new Vector3(0.1f, 0.1f, 0f),
                new Vector3(0.3f, 0.1f, 0f),
                new Vector3(0.1f, 0.3f, 0.2f),
            ],
            normals: [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
            texCoords: [Vector2.Zero, Vector2.UnitX, Vector2.UnitY],
            indices: [0, 1, 2],
            baseColorImageBytes: null,
            seamWeights);
        return ScannedRockPageBaker.Bake(
            primitive,
            pageSizeMeters: 4f,
            lod: 0,
            geometricError: 0f,
            materialPageId: 5).Single();
    }
}
