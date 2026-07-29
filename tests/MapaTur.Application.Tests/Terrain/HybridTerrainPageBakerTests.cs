using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class HybridTerrainPageBakerTests
{
    [Fact]
    public void should_pack_rock_blend_in_vertex_without_mask_texture()
    {
        // Arrange
        HybridTerrainMesh mesh = Create(
            positions:
            [
                new Vector3(1f, 1f, 10f),
                new Vector3(2f, 1f, 10f),
                new Vector3(1f, 2f, 11f),
            ],
            rockBlend: [0, 127, 255],
            indices: [0, 1, 2]);

        // Act
        HybridTerrainMeshPage page = HybridTerrainPageBaker.Bake(
            mesh,
            pageSizeMeters: 32f,
            lod: 0,
            geometricError: 0f).Single();

        // Assert
        new[] { page.VertexData[15], page.VertexData[35], page.VertexData[55] }
            .Should().Equal(0, 127, 255);
    }

    [Fact]
    public void should_partition_single_surface_without_adding_triangles()
    {
        // Arrange
        HybridTerrainMesh mesh = Create(
            positions:
            [
                new Vector3(1f, 1f, 10f),
                new Vector3(2f, 1f, 10f),
                new Vector3(1f, 2f, 11f),
                new Vector3(33f, 1f, 10f),
                new Vector3(34f, 1f, 10f),
                new Vector3(33f, 2f, 11f),
            ],
            rockBlend: [0, 0, 0, 255, 255, 255],
            indices: [0, 1, 2, 3, 4, 5]);

        // Act
        IReadOnlyList<HybridTerrainMeshPage> pages = HybridTerrainPageBaker.Bake(
            mesh,
            pageSizeMeters: 32f,
            lod: 0,
            geometricError: 0f);

        // Assert
        pages.Sum(page => page.IndexCount).Should().Be(mesh.Indices.Length);
    }

    [Fact]
    public void should_report_position_quantization_error_for_finest_page()
    {
        // Arrange
        HybridTerrainMesh mesh = Create(
            positions:
            [
                new Vector3(0f, 0f, 0f),
                new Vector3(32f, 0f, 0f),
                new Vector3(0f, 32f, 2.8f),
            ],
            rockBlend: [0, 127, 255],
            indices: [0, 1, 2]);

        // Act
        HybridTerrainMeshPage page = HybridTerrainPageBaker.Bake(
            mesh,
            pageSizeMeters: 32f,
            lod: 0,
            geometricError: 0f).Single();

        // Assert
        page.GeometricError.Should().BeGreaterThan(0f);
    }

    private static HybridTerrainMesh Create(
        Vector3[] positions,
        byte[] rockBlend,
        uint[] indices) =>
        new(
            positions,
            legacyPositions: positions.ToArray(),
            normals: Enumerable.Repeat(Vector3.UnitZ, positions.Length).ToArray(),
            orthoUvs: Enumerable.Repeat(new Vector2(0.25f, 0.75f), positions.Length).ToArray(),
            ambientOcclusion: Enumerable.Repeat(byte.MaxValue, positions.Length).ToArray(),
            rockBlend,
            materialVariants: Enumerable.Repeat((ushort)3, positions.Length).ToArray(),
            indices);
}
