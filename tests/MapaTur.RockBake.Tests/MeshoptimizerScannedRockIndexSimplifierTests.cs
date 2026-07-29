using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.RockBake;

namespace MapaTur.RockBake.Tests;

public sealed class MeshoptimizerScannedRockIndexSimplifierTests
{
    [Fact]
    public void should_preserve_every_topological_border_vertex()
    {
        // Arrange
        (float[] positions, uint[] indices, HashSet<uint> border) = CreateGrid(9);
        var simplifier = new MeshoptimizerScannedRockIndexSimplifier();
        var request = new ScannedRockSimplificationRequest(
            TargetIndexCount: 96,
            MaximumGeometricErrorMeters: 0.5f,
            LockBorder: true);

        // Act
        ScannedRockIndexSimplification result =
            simplifier.Simplify(indices, positions, positions.Length / 3, request);

        // Assert
        border.Should().BeSubsetOf(result.Indices);
    }

    [Fact]
    public void should_reduce_triangle_count_without_exceeding_absolute_error()
    {
        // Arrange
        (float[] positions, uint[] indices, _) = CreateGrid(17);
        var simplifier = new MeshoptimizerScannedRockIndexSimplifier();
        var request = new ScannedRockSimplificationRequest(
            TargetIndexCount: indices.Length / 4,
            MaximumGeometricErrorMeters: 0.2f,
            LockBorder: true);

        // Act
        ScannedRockIndexSimplification result =
            simplifier.Simplify(indices, positions, positions.Length / 3, request);

        // Assert
        (result.Indices.Length < indices.Length && result.GeometricErrorMeters <= 0.2f)
            .Should().BeTrue();
    }

    private static (float[] Positions, uint[] Indices, HashSet<uint> Border) CreateGrid(int size)
    {
        var positions = new float[size * size * 3];
        var border = new HashSet<uint>();
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int vertex = (y * size) + x;
                positions[(vertex * 3) + 0] = x * 0.25f;
                positions[(vertex * 3) + 1] = y * 0.25f;
                positions[(vertex * 3) + 2] =
                    0.08f * MathF.Sin(x * 0.8f) * MathF.Cos(y * 0.6f);
                if (x == 0 || y == 0 || x == size - 1 || y == size - 1)
                {
                    border.Add((uint)vertex);
                }
            }
        }

        var indices = new List<uint>((size - 1) * (size - 1) * 6);
        for (int y = 0; y < size - 1; y++)
        {
            for (int x = 0; x < size - 1; x++)
            {
                uint a = (uint)((y * size) + x);
                uint b = a + 1;
                uint c = a + (uint)size;
                uint d = c + 1;
                indices.Add(a);
                indices.Add(b);
                indices.Add(d);
                indices.Add(a);
                indices.Add(d);
                indices.Add(c);
            }
        }

        return (positions, indices.ToArray(), border);
    }
}
