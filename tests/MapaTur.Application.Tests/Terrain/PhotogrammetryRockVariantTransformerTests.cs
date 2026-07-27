using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class PhotogrammetryRockVariantTransformerTests
{
    [Fact]
    public void should_mirror_full_scan_geometry_without_changing_its_natural_outline()
    {
        // Arrange
        var source = new PhotogrammetryRockPrimitive(
            positions:
            [
                new Vector3(-2f, 0f, 0f),
                new Vector3(1f, 0f, 0.5f),
                new Vector3(0f, 2f, 1f),
            ],
            normals: Enumerable.Repeat(Vector3.Normalize(new Vector3(1f, 0f, 1f)), 3).ToArray(),
            texCoords: [Vector2.Zero, Vector2.UnitX, Vector2.UnitY],
            indices: [0, 1, 2],
            baseColorImageBytes: [1, 2, 3],
            seamWeights: [0, 127, 255]);

        // Act
        PhotogrammetryRockPrimitive mirrored =
            PhotogrammetryRockVariantTransformer.MirrorHorizontal(source);

        // Assert
        mirrored.Positions.Should().Equal(
            new Vector3(2f, 0f, 0f),
            new Vector3(-1f, 0f, 0.5f),
            new Vector3(0f, 2f, 1f));
        mirrored.Indices.Should().Equal(0, 2, 1);
    }

    [Fact]
    public void should_preserve_texture_uvs_and_seam_weights_when_mirroring()
    {
        // Arrange
        byte[] texture = [9, 8, 7];
        var source = new PhotogrammetryRockPrimitive(
            positions: [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
            normals: [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
            texCoords: [Vector2.Zero, Vector2.UnitX, Vector2.UnitY],
            indices: [0, 1, 2],
            baseColorImageBytes: texture,
            seamWeights: [0, 127, 255]);

        // Act
        PhotogrammetryRockPrimitive mirrored =
            PhotogrammetryRockVariantTransformer.MirrorHorizontal(source);

        // Assert
        mirrored.BaseColorImageBytes.Should().BeSameAs(texture);
        mirrored.TexCoords.Should().Equal(source.TexCoords);
        mirrored.SeamWeights.Should().Equal(source.SeamWeights);
    }
}
