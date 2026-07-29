using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class HybridTerrainSurfaceRegistryTests
{
    [Fact]
    public async Task should_not_publish_surface_until_worker_index_is_ready()
    {
        // Arrange
        HybridTerrainMeshPage page = Page(z: 2f);
        HybridTerrainPageKey key = new(page.PageX, page.PageY, page.Lod);
        var pending = new TaskCompletionSource<HybridTerrainSurfaceIndex>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registry = new HybridTerrainSurfaceRegistry((_, _) => pending.Task);
        Task registration = registry.RegisterAsync(key, page);

        // Act
        HybridTerrainRegisteredSurfaceSample? sample = registry.SampleHybridSurface(
            new Vector3(0.5f, 0.5f, 0f),
            maxDistanceMeters: 2.8f,
            out _);

        // Assert
        sample.Should().BeNull();
        pending.SetResult(new HybridTerrainSurfaceIndex(page));
        await registration;
    }

    [Fact]
    public async Task should_choose_nearest_surface_across_registered_pages()
    {
        // Arrange
        HybridTerrainMeshPage low = Page(z: 1f, pageX: 0);
        HybridTerrainMeshPage high = Page(z: 2.5f, pageX: 1);
        using var registry = new HybridTerrainSurfaceRegistry();
        await registry.RegisterAsync(new HybridTerrainPageKey(0, 0, 0), low);
        await registry.RegisterAsync(new HybridTerrainPageKey(1, 0, 0), high);

        // Act
        HybridTerrainRegisteredSurfaceSample? sample = registry.SampleHybridSurface(
            new Vector3(0.5f, 0.5f, 2.2f),
            maxDistanceMeters: 2.8f,
            out _);

        // Assert
        sample!.Value.PageKey.Should().Be(new HybridTerrainPageKey(1, 0, 0));
    }

    [Fact]
    public async Task should_stop_sampling_page_after_it_is_evicted()
    {
        // Arrange
        HybridTerrainMeshPage page = Page(z: 2f);
        HybridTerrainPageKey key = new(page.PageX, page.PageY, page.Lod);
        using var registry = new HybridTerrainSurfaceRegistry();
        await registry.RegisterAsync(key, page);

        // Act
        registry.Remove(key);
        HybridTerrainRegisteredSurfaceSample? sample = registry.SampleHybridSurface(
            new Vector3(0.5f, 0.5f, 0f),
            maxDistanceMeters: 2.8f,
            out _);

        // Assert
        sample.Should().BeNull();
    }

    private static HybridTerrainMeshPage Page(float z, int pageX = 0)
    {
        var positions = new[]
        {
            new Vector3(0f, 0f, z),
            new Vector3(2f, 0f, z),
            new Vector3(0f, 2f, z),
        };
        var mesh = new HybridTerrainMesh(
            positions,
            legacyPositions: positions.Select(position => new Vector3(position.X, position.Y, 0f)).ToArray(),
            normals: Enumerable.Repeat(Vector3.UnitZ, positions.Length).ToArray(),
            orthoUvs: Enumerable.Repeat(Vector2.Zero, positions.Length).ToArray(),
            ambientOcclusion: Enumerable.Repeat(byte.MaxValue, positions.Length).ToArray(),
            rockBlend: Enumerable.Repeat(byte.MaxValue, positions.Length).ToArray(),
            materialVariants: new ushort[positions.Length],
            indices: [0, 1, 2]);
        HybridTerrainMeshPage baked = HybridTerrainPageBaker.Bake(
            mesh,
            pageSizeMeters: 32f,
            lod: 0,
            geometricError: 0f).Single();
        return new HybridTerrainMeshPage(
            baked.Lod,
            pageX,
            baked.PageY,
            baked.WorldMin,
            baked.WorldExtent,
            baked.GeometricError,
            baked.VertexData,
            baked.Indices);
    }
}
