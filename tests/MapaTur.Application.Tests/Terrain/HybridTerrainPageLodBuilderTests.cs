using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class HybridTerrainPageLodBuilderTests
{
    [Fact]
    public void should_build_coarser_lod_from_original_hybrid_vertex_payload()
    {
        // Arrange
        byte[] vertices = new byte[HybridTerrainMeshPage.VertexStrideBytes * 4];
        vertices[15] = 0;
        vertices[35] = 80;
        vertices[55] = 160;
        vertices[75] = 255;
        var source = new HybridTerrainMeshPage(
            lod: 0,
            pageX: 0,
            pageY: 0,
            worldMin: Vector3.Zero,
            worldExtent: new Vector3(10f, 10f, 2f),
            geometricError: 0.001f,
            vertexData: vertices,
            indices: [0, 1, 2, 0, 2, 3]);
        var simplifier = new FixedSimplifier([0, 1, 2], error: 0.1f);

        // Act
        HybridTerrainMeshPage result = HybridTerrainPageLodBuilder.Build(
            source,
            lod: 1,
            targetTriangleFraction: 0.5f,
            maximumGeometricErrorMeters: 0.35f,
            simplifier);

        // Assert
        new[] { result.VertexData[15], result.VertexData[35], result.VertexData[55] }
            .Should().Equal(0, 80, 160);
    }

    private sealed class FixedSimplifier(uint[] indices, float error) : IScannedRockIndexSimplifier
    {
        public ScannedRockIndexSimplification Simplify(
            ReadOnlySpan<uint> sourceIndices,
            ReadOnlySpan<float> positions,
            int vertexCount,
            ScannedRockSimplificationRequest request) =>
            new(indices, error);
    }
}
