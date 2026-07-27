using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class ScannedRockPageStoreTests
{
    [Fact]
    public void should_roundtrip_gpu_ready_rmp2_page()
    {
        // Arrange
        byte[] vertices = Enumerable.Range(0, ScannedRockMeshPage.VertexStrideBytes * 3)
            .Select(value => (byte)value)
            .ToArray();
        var expected = new ScannedRockMeshPage(
            lod: 1,
            pageX: -4,
            pageY: 7,
            worldMin: new Vector3(-64f, 112f, 1900f),
            worldExtent: new Vector3(16f, 16f, 80f),
            geometricError: 0.02f,
            materialPageId: 12,
            vertexData: vertices,
            indices: [0, 1, 2]);
        using var stream = new MemoryStream();

        // Act
        ScannedRockMeshPageStore.Write(stream, expected);
        stream.Position = 0;
        ScannedRockMeshPage actual = ScannedRockMeshPageStore.Read(stream);

        // Assert
        actual.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void should_reject_rmp1_vertex_stride_for_photogrammetry()
    {
        // Arrange
        byte[] legacyVertices = new byte[RockMeshPage.VertexStrideBytes * 3];

        // Act
        Action act = () => _ = new ScannedRockMeshPage(
            lod: 0,
            pageX: 0,
            pageY: 0,
            worldMin: Vector3.Zero,
            worldExtent: Vector3.One,
            geometricError: 0f,
            materialPageId: 0,
            vertexData: legacyVertices,
            indices: [0, 1, 2]);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void should_read_only_fixed_header_when_indexing_rmp2_page()
    {
        // Arrange
        var expected = new ScannedRockMeshPage(
            lod: 2,
            pageX: -3,
            pageY: 8,
            worldMin: new Vector3(-48f, 128f, 1800f),
            worldExtent: new Vector3(16f, 16f, 90f),
            geometricError: 0.08f,
            materialPageId: 4,
            vertexData: new byte[ScannedRockMeshPage.VertexStrideBytes * 3],
            indices: [0, 1, 2]);
        using var stream = new MemoryStream();
        ScannedRockMeshPageStore.Write(stream, expected);
        stream.Position = 0;

        // Act
        ScannedRockMeshPageHeader header = ScannedRockMeshPageStore.ReadHeader(stream);

        // Assert
        stream.Position.Should().Be(ScannedRockMeshPageStore.HeaderBytes);
        header.Should().BeEquivalentTo(new
        {
            expected.Lod,
            expected.PageX,
            expected.PageY,
            expected.WorldMin,
            expected.WorldExtent,
            expected.GeometricError,
            expected.MaterialPageId,
            expected.VertexCount,
            expected.IndexCount,
        });
    }
}
