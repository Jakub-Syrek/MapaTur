using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class ScannedRockPageCatalogTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"mapatur-rock-catalog-{Guid.NewGuid():N}");

    [Fact]
    public async Task should_index_headers_without_loading_page_payloads()
    {
        // Arrange
        Directory.CreateDirectory(root);
        WritePage(CreatePage(0, 2, -1, 0.005f));
        WritePage(CreatePage(1, 2, -1, 0.02f));
        WritePage(CreatePage(2, 2, -1, 0.08f));

        // Act
        ScannedRockPageCatalog catalog = await ScannedRockPageCatalog.LoadAsync(root);

        // Assert
        catalog.Pages.Should().HaveCount(3);
        catalog.Pages.Should().OnlyContain(page => page.ResidentBytes == 66);
    }

    private static ScannedRockMeshPage CreatePage(byte lod, int pageX, int pageY, float error) =>
        new(
            lod,
            pageX,
            pageY,
            new Vector3(pageX * 16f, pageY * 16f, 1800f),
            new Vector3(16f, 16f, 80f),
            error,
            7,
            new byte[ScannedRockMeshPage.VertexStrideBytes * 3],
            [0, 1, 2]);

    private void WritePage(ScannedRockMeshPage page)
    {
        string path = Path.Combine(root, ScannedRockMeshPageStore.RelativePathFor(page.Lod, page.PageX, page.PageY));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using FileStream stream = File.Create(path);
        ScannedRockMeshPageStore.Write(stream, page);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
