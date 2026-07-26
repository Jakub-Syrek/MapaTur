using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class RockMeshPageIndexTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"mapatur-rock-index-{Guid.NewGuid():N}");

    [Fact]
    public async Task should_choose_near_lod0_and_far_lod2_for_the_same_update()
    {
        // Arrange
        CreatePagePlaceholder(lod: 0, pageX: 0, pageY: 0);
        CreatePagePlaceholder(lod: 1, pageX: 0, pageY: 0);
        CreatePagePlaceholder(lod: 2, pageX: 0, pageY: 0);
        CreatePagePlaceholder(lod: 0, pageX: 6, pageY: 0);
        CreatePagePlaceholder(lod: 1, pageX: 6, pageY: 0);
        CreatePagePlaceholder(lod: 2, pageX: 6, pageY: 0);
        RockMeshPageIndex index = await RockMeshPageIndex.LoadAsync(root);

        // Act
        IReadOnlyList<RockMeshPageDescriptor> selected = index.Select(
            focus: Vector3.Zero,
            pageSizeMeters: 16f,
            prefetchRadiusMeters: 160f);

        // Assert
        selected.Select(page => page.Lod).Should().Equal(0, 2);
    }

    [Fact]
    public async Task should_ignore_files_outside_the_rmp_spatial_layout()
    {
        // Arrange
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "manifest.json"), "{}");
        CreatePagePlaceholder(lod: 1, pageX: -2, pageY: 3);

        // Act
        RockMeshPageIndex index = await RockMeshPageIndex.LoadAsync(root);

        // Assert
        index.Pages.Should().ContainSingle();
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private void CreatePagePlaceholder(byte lod, int pageX, int pageY)
    {
        string path = Path.Combine(root, RockMeshPageStore.RelativePathFor(lod, pageX, pageY));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
    }
}
