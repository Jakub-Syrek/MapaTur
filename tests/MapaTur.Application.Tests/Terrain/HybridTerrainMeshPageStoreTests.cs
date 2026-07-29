using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class HybridTerrainMeshPageStoreTests
{
    [Fact]
    public void should_roundtrip_directly_uploadable_rmp3_page()
    {
        // Arrange
        var expected = new HybridTerrainMeshPage(
            lod: 1,
            pageX: -4,
            pageY: 7,
            worldMin: new Vector3(-64f, 112f, 1900f),
            worldExtent: new Vector3(64f, 64f, 80f),
            geometricError: 0.35f,
            vertexData: Enumerable.Range(0, HybridTerrainMeshPage.VertexStrideBytes * 3)
                .Select(value => (byte)value)
                .ToArray(),
            indices: [0, 1, 2]);
        using var stream = new MemoryStream();

        // Act
        HybridTerrainMeshPageStore.Write(stream, expected);
        stream.Position = 0;
        HybridTerrainMeshPage actual = HybridTerrainMeshPageStore.Read(stream);

        // Assert
        actual.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void should_leave_stream_at_payload_after_reading_rmp3_header()
    {
        // Arrange
        var page = new HybridTerrainMeshPage(
            lod: 2,
            pageX: 1,
            pageY: 2,
            worldMin: Vector3.Zero,
            worldExtent: Vector3.One,
            geometricError: 1.2f,
            vertexData: new byte[HybridTerrainMeshPage.VertexStrideBytes * 3],
            indices: [0, 1, 2]);
        using var stream = new MemoryStream();
        HybridTerrainMeshPageStore.Write(stream, page);
        stream.Position = 0;

        // Act
        _ = HybridTerrainMeshPageStore.ReadHeader(stream);

        // Assert
        stream.Position.Should().Be(HybridTerrainMeshPageStore.HeaderBytes);
    }
}
