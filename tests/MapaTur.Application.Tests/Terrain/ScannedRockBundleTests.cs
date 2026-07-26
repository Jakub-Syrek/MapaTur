using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class ScannedRockBundleTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"mapatur-rmp2-bundle-{Guid.NewGuid():N}");

    [Fact]
    public async Task should_load_gpu_ready_geometry_and_material_off_thread()
    {
        // Arrange
        Directory.CreateDirectory(root);
        var page = new ScannedRockMeshPage(
            0,
            0,
            0,
            Vector3.Zero,
            Vector3.One,
            0f,
            3,
            new byte[ScannedRockMeshPage.VertexStrideBytes * 3],
            [0, 1, 2]);
        string geometryPath = Path.Combine(root, ScannedRockMeshPageStore.RelativePathFor(0, 0, 0));
        Directory.CreateDirectory(Path.GetDirectoryName(geometryPath)!);
        using (FileStream stream = File.Create(geometryPath))
        {
            ScannedRockMeshPageStore.Write(stream, page);
        }

        byte[] rgba = Enumerable.Repeat(new byte[] { 80, 90, 100, 255 }, 4 * 4)
            .SelectMany(pixel => pixel)
            .ToArray();
        using (FileStream stream = File.Create(Path.Combine(root, "3.rtex")))
        {
            RockMaterialPageStore.Write(stream, RockMaterialPageBaker.Bake(3, rgba, 4, 4));
        }

        // Act
        ScannedRockBundle bundle = await ScannedRockBundle.LoadAsync(root);

        // Assert
        bundle.Pages.Should().ContainSingle(page => page.MaterialPageId == bundle.Materials.Single().PageId);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
