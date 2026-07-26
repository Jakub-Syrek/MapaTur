using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class RockMeshPageStoreTests
{
    [Fact]
    public void should_round_trip_gpu_ready_page()
    {
        // Arrange
        byte[] vertices = Enumerable.Range(0, RockMeshPage.VertexStrideBytes * 3)
            .Select(i => (byte)i)
            .ToArray();
        var page = new RockMeshPage(
            lod: 1,
            pageX: 17,
            pageY: -8,
            worldMin: new Vector3(10f, 20f, 30f),
            worldExtent: new Vector3(32f, 32f, 19f),
            geometricError: 0.5f,
            vertexData: vertices,
            indices: new ushort[] { 0, 1, 2 });
        using var stream = new MemoryStream();

        // Act
        RockMeshPageStore.Write(stream, page);
        stream.Position = 0;
        RockMeshPage read = RockMeshPageStore.Read(stream);

        // Assert
        read.Should().BeEquivalentTo(page);
    }

    [Fact]
    public void should_reject_a_page_above_the_u16_vertex_limit()
    {
        // Arrange
        byte[] vertices = new byte[(RockMeshPage.MaxVertices + 1) * RockMeshPage.VertexStrideBytes];

        // Act
        Action act = () => _ = new RockMeshPage(
            0,
            0,
            0,
            Vector3.Zero,
            Vector3.One,
            0.25f,
            vertices,
            Array.Empty<ushort>());

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void should_reject_a_non_rmp_stream()
    {
        // Arrange
        using var stream = new MemoryStream("NOPE"u8.ToArray());

        // Act
        Action act = () => RockMeshPageStore.Read(stream);

        // Assert
        act.Should().Throw<InvalidDataException>();
    }
}
