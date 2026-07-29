using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class HybridTerrainMeshTests
{
    [Fact]
    public void should_reject_vertex_beyond_relief_limit()
    {
        // Arrange
        Vector3[] legacy =
        [
            Vector3.Zero,
            Vector3.UnitX,
            Vector3.UnitY,
        ];
        Vector3[] displaced =
        [
            new Vector3(0f, 0f, HybridTerrainMesh.DefaultMaxReliefMeters + 0.01f),
            Vector3.UnitX,
            Vector3.UnitY,
        ];

        // Act
        Action build = () => Create(displaced, legacy);

        // Assert
        build.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static HybridTerrainMesh Create(Vector3[] positions, Vector3[] legacyPositions) =>
        new(
            positions,
            legacyPositions,
            normals: Enumerable.Repeat(Vector3.UnitZ, positions.Length).ToArray(),
            orthoUvs: Enumerable.Repeat(Vector2.Zero, positions.Length).ToArray(),
            ambientOcclusion: Enumerable.Repeat(byte.MaxValue, positions.Length).ToArray(),
            rockBlend: new byte[positions.Length],
            materialVariants: new ushort[positions.Length],
            indices: [0, 1, 2]);
}
