using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class HybridTerrainMeshComposerTests
{
    [Fact]
    public void should_replace_owned_dem_triangle_instead_of_appending_overlay()
    {
        // Arrange
        HybridTerrainMesh terrain = Create(
            positions:
            [
                new Vector3(0f, 0f, 0f),
                new Vector3(1f, 0f, 0f),
                new Vector3(1f, 1f, 0f),
                new Vector3(0f, 1f, 0f),
            ],
            blend: 0,
            indices: [0, 1, 2, 0, 2, 3]);
        HybridTerrainMesh replacement = Create(
            positions:
            [
                new Vector3(0f, 0f, 0f),
                new Vector3(1f, 0f, 0f),
                new Vector3(1f, 1f, 0f),
            ],
            blend: byte.MaxValue,
            indices: [0, 1, 2]);

        // Act
        HybridTerrainMesh hybrid = HybridTerrainMeshComposer.ReplaceTriangles(
            terrain,
            replacement,
            replacedTerrainTriangles: [0]);

        // Assert
        hybrid.TriangleCount.Should().Be(2);
    }

    [Fact]
    public void should_reject_replacement_with_non_welded_boundary()
    {
        // Arrange
        HybridTerrainMesh terrain = Create(
            positions:
            [
                new Vector3(0f, 0f, 0f),
                new Vector3(1f, 0f, 0f),
                new Vector3(1f, 1f, 0f),
                new Vector3(0f, 1f, 0f),
            ],
            blend: 0,
            indices: [0, 1, 2, 0, 2, 3]);
        HybridTerrainMesh replacement = Create(
            positions:
            [
                new Vector3(0f, 0f, 0.01f),
                new Vector3(1f, 0f, 0f),
                new Vector3(1f, 1f, 0f),
            ],
            blend: byte.MaxValue,
            indices: [0, 1, 2]);

        // Act
        Action compose = () => HybridTerrainMeshComposer.ReplaceTriangles(
            terrain,
            replacement,
            replacedTerrainTriangles: [0]);

        // Assert
        compose.Should().Throw<InvalidOperationException>();
    }

    private static HybridTerrainMesh Create(Vector3[] positions, byte blend, uint[] indices) =>
        new(
            positions,
            legacyPositions: positions.ToArray(),
            normals: Enumerable.Repeat(Vector3.UnitZ, positions.Length).ToArray(),
            orthoUvs: Enumerable.Repeat(Vector2.Zero, positions.Length).ToArray(),
            ambientOcclusion: Enumerable.Repeat(byte.MaxValue, positions.Length).ToArray(),
            rockBlend: Enumerable.Repeat(blend, positions.Length).ToArray(),
            materialVariants: new ushort[positions.Length],
            indices);
}
