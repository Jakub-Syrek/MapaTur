using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class ScannedRockIncrementalPageWriterTests
{
    [Fact]
    public void should_merge_same_page_without_retaining_geometry_in_memory()
    {
        // Arrange
        string root = Path.Combine(Path.GetTempPath(), $"mapatur-rock-pages-{Guid.NewGuid():N}");
        try
        {
            var writer = new ScannedRockIncrementalPageWriter(root);

            // Act
            writer.Add(BakeTriangle(xOffset: 0.1f));
            writer.Add(BakeTriangle(xOffset: 1.1f));

            // Assert
            writer.PageCount.Should().Be(1);
            writer.TriangleCount.Should().Be(2);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void should_persist_a_readable_combined_page()
    {
        // Arrange
        string root = Path.Combine(Path.GetTempPath(), $"mapatur-rock-pages-{Guid.NewGuid():N}");
        try
        {
            var writer = new ScannedRockIncrementalPageWriter(root);
            ScannedRockMeshPage first = BakeTriangle(xOffset: 0.1f);

            // Act
            writer.Add(first);
            writer.Add(BakeTriangle(xOffset: 1.1f));

            // Assert
            string relative = ScannedRockMeshPageStore.RelativePathFor(
                first.Lod,
                first.PageX,
                first.PageY);
            using FileStream stream = File.OpenRead(Path.Combine(root, relative));
            ScannedRockMeshPageStore.Read(stream).IndexCount.Should().Be(6);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
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
