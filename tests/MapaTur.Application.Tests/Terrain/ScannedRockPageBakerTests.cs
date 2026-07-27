using System.Buffers.Binary;
using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class ScannedRockPageBakerTests
{
    [Fact]
    public void should_partition_scan_triangles_into_spatial_pages()
    {
        // Arrange
        var primitive = new PhotogrammetryRockPrimitive(
            positions:
            [
                new Vector3(1f, 1f, 10f),
                new Vector3(2f, 1f, 10f),
                new Vector3(1f, 2f, 11f),
                new Vector3(17f, 1f, 10f),
                new Vector3(18f, 1f, 10f),
                new Vector3(17f, 2f, 11f),
            ],
            normals: Enumerable.Repeat(Vector3.UnitY, 6).ToArray(),
            texCoords: Enumerable.Repeat(Vector2.Zero, 6).ToArray(),
            indices: [0, 1, 2, 3, 4, 5],
            baseColorImageBytes: null);

        // Act
        IReadOnlyList<ScannedRockMeshPage> pages = ScannedRockPageBaker.Bake(
            primitive,
            pageSizeMeters: 16f,
            lod: 0,
            geometricError: 0f,
            materialPageId: 9);

        // Assert
        pages.Select(page => page.PageX).Should().Equal(0, 1);
    }

    [Fact]
    public void should_pack_scan_uv_and_material_for_direct_gpu_upload()
    {
        // Arrange
        var primitive = new PhotogrammetryRockPrimitive(
            positions:
            [
                new Vector3(1f, 1f, 10f),
                new Vector3(2f, 1f, 10f),
                new Vector3(1f, 2f, 11f),
            ],
            normals: [Vector3.UnitY, Vector3.UnitY, Vector3.UnitY],
            texCoords: [new Vector2(0.25f, 0.75f), Vector2.Zero, Vector2.One],
            indices: [0, 1, 2],
            baseColorImageBytes: null);

        // Act
        ScannedRockMeshPage page = ScannedRockPageBaker.Bake(
            primitive,
            pageSizeMeters: 16f,
            lod: 0,
            geometricError: 0f,
            materialPageId: 37).Single();

        // Assert
        BinaryPrimitives.ReadUInt16LittleEndian(page.VertexData.AsSpan(16, 2)).Should().Be(37);
    }

    [Fact]
    public void should_pack_offline_seam_weight_for_edge_feathering()
    {
        // Arrange
        var primitive = new PhotogrammetryRockPrimitive(
            positions:
            [
                new Vector3(1f, 1f, 10f),
                new Vector3(2f, 1f, 10f),
                new Vector3(1f, 2f, 11f),
            ],
            normals: [Vector3.UnitY, Vector3.UnitY, Vector3.UnitY],
            texCoords: [Vector2.Zero, Vector2.UnitX, Vector2.One],
            indices: [0, 1, 2],
            baseColorImageBytes: null,
            seamWeights: [0, 127, 255]);

        // Act
        ScannedRockMeshPage page = ScannedRockPageBaker.Bake(
            primitive,
            pageSizeMeters: 16f,
            lod: 0,
            geometricError: 0f,
            materialPageId: 1).Single();

        // Assert
        page.VertexData[15].Should().Be(0);
    }
}
