using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class HybridTerrainSurfaceSamplerTests
{
    [Fact]
    public void should_project_legacy_anchor_onto_nearest_hybrid_triangle()
    {
        // Arrange
        HybridTerrainMesh mesh = Create(
            positions:
            [
                new Vector3(0f, 0f, 2f),
                new Vector3(2f, 0f, 2f),
                new Vector3(0f, 2f, 2f),
            ]);

        // Act
        HybridTerrainSurfaceSample? sample = HybridTerrainSurfaceSampler.SampleHybridSurface(
            mesh,
            legacyPoint: new Vector3(0.5f, 0.5f, 0f),
            maxDistanceMeters: HybridTerrainMesh.DefaultMaxReliefMeters);

        // Assert
        Vector3.Distance(sample!.Value.Position, new Vector3(0.5f, 0.5f, 2f))
            .Should().BeLessThan(1e-5f);
    }

    [Fact]
    public void should_return_absent_when_surface_is_outside_allowed_relief()
    {
        // Arrange
        HybridTerrainMesh mesh = Create(
            positions:
            [
                new Vector3(0f, 0f, 2f),
                new Vector3(2f, 0f, 2f),
                new Vector3(0f, 2f, 2f),
            ]);

        // Act
        HybridTerrainSurfaceSample? sample = HybridTerrainSurfaceSampler.SampleHybridSurface(
            mesh,
            legacyPoint: new Vector3(0.5f, 0.5f, 0f),
            maxDistanceMeters: 1.99f);

        // Assert
        sample.Should().BeNull();
    }

    [Fact]
    public void should_choose_closest_surface_when_faces_overlap_in_xy()
    {
        // Arrange
        Vector3[] positions =
        [
            new Vector3(0f, 0f, 1f),
            new Vector3(2f, 0f, 1f),
            new Vector3(0f, 2f, 1f),
            new Vector3(0f, 0f, 2.5f),
            new Vector3(2f, 0f, 2.5f),
            new Vector3(0f, 2f, 2.5f),
        ];
        HybridTerrainMesh mesh = Create(positions, indices: [0, 1, 2, 3, 4, 5]);

        // Act
        HybridTerrainSurfaceSample? sample = HybridTerrainSurfaceSampler.SampleHybridSurface(
            mesh,
            legacyPoint: new Vector3(0.5f, 0.5f, 2.2f),
            maxDistanceMeters: 2.8f);

        // Assert
        sample!.Value.TriangleIndex.Should().Be(1);
    }

    [Fact]
    public void indexed_query_should_match_exhaustive_surface_sample()
    {
        // Arrange
        Vector3[] positions =
        [
            new Vector3(0f, 0f, 1f),
            new Vector3(2f, 0f, 1f),
            new Vector3(0f, 2f, 1f),
            new Vector3(0f, 0f, 2.5f),
            new Vector3(2f, 0f, 2.5f),
            new Vector3(0f, 2f, 2.5f),
        ];
        HybridTerrainMesh mesh = Create(positions, indices: [0, 1, 2, 3, 4, 5]);
        var index = new HybridTerrainSurfaceIndex(mesh);
        Vector3 legacyPoint = new(0.5f, 0.5f, 2.2f);
        HybridTerrainSurfaceSample? expected = HybridTerrainSurfaceSampler.SampleHybridSurface(
            mesh,
            legacyPoint,
            maxDistanceMeters: 2.8f);

        // Act
        HybridTerrainSurfaceSample? actual = HybridTerrainSurfaceSampler.SampleHybridSurface(
            index,
            legacyPoint,
            maxDistanceMeters: 2.8f,
            out _);

        // Assert
        actual.Should().Be(expected);
    }

    [Fact]
    public void indexed_query_should_prune_distant_triangle_groups()
    {
        // Arrange
        HybridTerrainMesh mesh = CreateSeparatedTriangles(count: 256, spacingMeters: 10f);
        var index = new HybridTerrainSurfaceIndex(mesh);

        // Act
        _ = HybridTerrainSurfaceSampler.SampleHybridSurface(
            index,
            legacyPoint: new Vector3(0.25f, 0.25f, 0f),
            maxDistanceMeters: 2.8f,
            out HybridTerrainSurfaceQueryDiagnostics diagnostics);

        // Assert
        diagnostics.TriangleTests.Should().BeLessThan(16);
    }

    [Fact]
    public void indexed_query_should_sample_directly_uploadable_rmp3_page()
    {
        // Arrange
        HybridTerrainMesh mesh = Create(
            positions:
            [
                new Vector3(0f, 0f, 2f),
                new Vector3(2f, 0f, 2f),
                new Vector3(0f, 2f, 2f),
            ]);
        HybridTerrainMeshPage page = HybridTerrainPageBaker.Bake(
            mesh,
            pageSizeMeters: 32f,
            lod: 0,
            geometricError: 0f).Single();
        var index = new HybridTerrainSurfaceIndex(page);

        // Act
        HybridTerrainSurfaceSample? sample = HybridTerrainSurfaceSampler.SampleHybridSurface(
            index,
            legacyPoint: new Vector3(0.5f, 0.5f, 0f),
            maxDistanceMeters: 2.8f,
            out _);

        // Assert
        Vector3.Distance(sample!.Value.Position, new Vector3(0.5f, 0.5f, 2f))
            .Should().BeLessThan(0.001f);
    }

    private static HybridTerrainMesh Create(Vector3[] positions, uint[]? indices = null) =>
        new(
            positions,
            legacyPositions: positions.Select(position => new Vector3(position.X, position.Y, 0f)).ToArray(),
            normals: Enumerable.Repeat(Vector3.UnitZ, positions.Length).ToArray(),
            orthoUvs: Enumerable.Repeat(Vector2.Zero, positions.Length).ToArray(),
            ambientOcclusion: Enumerable.Repeat(byte.MaxValue, positions.Length).ToArray(),
            rockBlend: Enumerable.Repeat(byte.MaxValue, positions.Length).ToArray(),
            materialVariants: new ushort[positions.Length],
            indices ?? [0, 1, 2]);

    private static HybridTerrainMesh CreateSeparatedTriangles(int count, float spacingMeters)
    {
        var positions = new Vector3[count * 3];
        var indices = new uint[count * 3];
        for (int triangle = 0; triangle < count; triangle++)
        {
            float x = triangle * spacingMeters;
            int offset = triangle * 3;
            positions[offset] = new Vector3(x, 0f, 1f);
            positions[offset + 1] = new Vector3(x + 1f, 0f, 1f);
            positions[offset + 2] = new Vector3(x, 1f, 1f);
            indices[offset] = checked((uint)offset);
            indices[offset + 1] = checked((uint)(offset + 1));
            indices[offset + 2] = checked((uint)(offset + 2));
        }

        return Create(positions, indices);
    }
}
