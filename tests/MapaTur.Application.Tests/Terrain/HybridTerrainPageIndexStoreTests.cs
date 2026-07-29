using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class HybridTerrainPageIndexStoreTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"mapatur-hybrid-index-{Guid.NewGuid():N}");

    [Fact]
    public void should_round_trip_hierarchy_descriptors_without_reading_geometry_payloads()
    {
        // Arrange
        Directory.CreateDirectory(root);
        HybridTerrainPageDescriptor[] expected =
        [
            Descriptor(lod: 0, pageX: -4, pageY: 7),
            Descriptor(lod: 1, pageX: -2, pageY: 3),
            Descriptor(lod: 2, pageX: -1, pageY: 1),
        ];

        // Act
        HybridTerrainPageIndexStore.Write(root, expected);
        IReadOnlyList<HybridTerrainPageDescriptor> restored =
            HybridTerrainPageIndexStore.Read(root);

        // Assert
        restored.Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task catalog_should_use_prebaked_index_when_page_payload_is_absent()
    {
        // Arrange
        Directory.CreateDirectory(root);
        HybridTerrainPageDescriptor descriptor = Descriptor(lod: 2, pageX: 3, pageY: 5);
        HybridTerrainPageIndexStore.Write(root, [descriptor]);

        // Act
        HybridTerrainPageCatalog catalog = await HybridTerrainPageCatalog.LoadAsync(root);

        // Assert
        catalog.Pages.Should().ContainSingle().Which.Should().Be(descriptor);
    }

    [Fact]
    public void should_reject_duplicate_page_keys_before_publishing_index()
    {
        // Arrange
        Directory.CreateDirectory(root);
        HybridTerrainPageDescriptor descriptor = Descriptor(lod: 0, pageX: 3, pageY: 5);

        // Act
        Action write = () => HybridTerrainPageIndexStore.Write(root, [descriptor, descriptor]);

        // Assert
        write.Should().Throw<InvalidDataException>();
    }

    private HybridTerrainPageDescriptor Descriptor(byte lod, int pageX, int pageY) =>
        new(
            new HybridTerrainPageKey(pageX, pageY, lod),
            new Vector3(pageX * 32f, pageY * 32f, 1800f),
            new Vector3(32f * (1 << lod), 32f * (1 << lod), 50f),
            geometricError: lod switch { 0 => 0.01f, 1 => 0.35f, _ => 1.2f },
            vertexCount: 120,
            indexCount: 300,
            Path.Combine(root, HybridTerrainMeshPageStore.RelativePathFor(lod, pageX, pageY)));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
