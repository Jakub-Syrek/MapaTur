using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class ScannedRockPageIndexStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"mapatur-rock-index-{Guid.NewGuid():N}");

    [Fact]
    public void should_round_trip_page_descriptors_without_reading_geometry_payloads()
    {
        // Arrange
        Directory.CreateDirectory(root);
        ScannedRockPageDescriptor[] expected =
        [
            Descriptor(pageX: -4, pageY: 7),
            Descriptor(pageX: 11, pageY: -9),
        ];

        // Act
        ScannedRockPageIndexStore.Write(root, expected);
        IReadOnlyList<ScannedRockPageDescriptor> restored =
            ScannedRockPageIndexStore.Read(root);

        // Assert
        restored.Select(page => page.Key).Should().Equal(expected.Select(page => page.Key));
        restored.Select(page => page.WorldMin).Should().Equal(expected.Select(page => page.WorldMin));
        restored.Select(page => page.VertexCount).Should().Equal(expected.Select(page => page.VertexCount));
    }

    [Fact]
    public async Task catalog_should_use_prebaked_index_when_page_file_is_absent()
    {
        // Arrange
        Directory.CreateDirectory(root);
        ScannedRockPageDescriptor descriptor = Descriptor(pageX: 3, pageY: 5);
        ScannedRockPageIndexStore.Write(root, [descriptor]);

        // Act
        ScannedRockPageCatalog catalog = await ScannedRockPageCatalog.LoadAsync(root);

        // Assert
        catalog.Pages.Single().Key.Should().Be(descriptor.Key);
    }

    private ScannedRockPageDescriptor Descriptor(int pageX, int pageY)
    {
        byte lod = 0;
        return new ScannedRockPageDescriptor(
            new ScannedRockPageKey(pageX, pageY, lod),
            new Vector3(pageX * 32f, pageY * 32f, 1800f),
            new Vector3(32f, 32f, 50f),
            geometricError: 5f,
            materialPageId: 20,
            vertexCount: 120,
            indexCount: 300,
            Path.Combine(root, ScannedRockMeshPageStore.RelativePathFor(lod, pageX, pageY)));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
