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
}
