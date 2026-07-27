using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class ScannedRockMeshPageCombinerTests
{
    [Fact]
    public void should_combine_two_chunks_that_land_in_the_same_streaming_page()
    {
        // Arrange
        ScannedRockMeshPage first = BakeTriangle(xOffset: 0.1f);
        ScannedRockMeshPage second = BakeTriangle(xOffset: 1.1f);

        // Act
        ScannedRockMeshPage combined = ScannedRockMeshPageCombiner.Combine(first, second);

        // Assert
        combined.IndexCount.Should().Be(first.IndexCount + second.IndexCount);
        combined.VertexCount.Should().Be(first.VertexCount + second.VertexCount);
    }

    [Fact]
    public void should_preserve_page_identity_material_and_combined_world_bounds()
    {
        // Arrange
        ScannedRockMeshPage first = BakeTriangle(xOffset: 0.1f);
        ScannedRockMeshPage second = BakeTriangle(xOffset: 1.1f);

        // Act
        ScannedRockMeshPage combined = ScannedRockMeshPageCombiner.Combine(first, second);

        // Assert
        combined.PageX.Should().Be(first.PageX);
        combined.PageY.Should().Be(first.PageY);
        combined.Lod.Should().Be(first.Lod);
        combined.MaterialPageId.Should().Be(first.MaterialPageId);
        combined.WorldMin.X.Should().BeApproximately(0.1f, 0.001f);
        combined.WorldMin.X.Should().BeLessThan(combined.WorldMin.X + combined.WorldExtent.X);
    }

    private static ScannedRockMeshPage BakeTriangle(float xOffset)
    {
        var primitive = new PhotogrammetryRockPrimitive(
            positions:
            [
                new Vector3(xOffset, 0.1f, 0f),
                new Vector3(xOffset + 0.2f, 0.1f, 0f),
                new Vector3(xOffset, 0.3f, 0.2f),
            ],
            normals: [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
            texCoords: [Vector2.Zero, Vector2.UnitX, Vector2.UnitY],
            indices: [0, 1, 2],
            baseColorImageBytes: null,
            seamWeights: [0, 127, 255]);
        return ScannedRockPageBaker.Bake(
            primitive,
            pageSizeMeters: 4f,
            lod: 0,
            geometricError: 0f,
            materialPageId: 5).Single();
    }
}
