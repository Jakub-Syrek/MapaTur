using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class RockMaterialPageTests
{
    [Fact]
    public void should_prebake_complete_bc1_mip_chain()
    {
        // Arrange
        byte[] rgba = Enumerable.Repeat(new byte[] { 92, 98, 101, 255 }, 8 * 8)
            .SelectMany(pixel => pixel)
            .ToArray();

        // Act
        RockMaterialPage page = RockMaterialPageBaker.Bake(pageId: 7, rgba, width: 8, height: 8);

        // Assert
        page.MipCount.Should().Be(4);
    }

    [Fact]
    public void should_roundtrip_gpu_ready_rock_texture()
    {
        // Arrange
        byte[] rgba = Enumerable.Repeat(new byte[] { 92, 98, 101, 255 }, 8 * 8)
            .SelectMany(pixel => pixel)
            .ToArray();
        RockMaterialPage expected = RockMaterialPageBaker.Bake(pageId: 7, rgba, width: 8, height: 8);
        using var stream = new MemoryStream();

        // Act
        RockMaterialPageStore.Write(stream, expected);
        stream.Position = 0;
        RockMaterialPage actual = RockMaterialPageStore.Read(stream);

        // Assert
        actual.Should().BeEquivalentTo(expected);
    }
}
