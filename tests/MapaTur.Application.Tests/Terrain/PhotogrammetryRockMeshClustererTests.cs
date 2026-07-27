using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class PhotogrammetryRockMeshClustererTests
{
    [Fact]
    public void should_reduce_dense_geometry_without_emitting_invalid_triangles()
    {
        // Arrange
        PhotogrammetryRockPrimitive dense = CreateGrid(size: 17, spacing: 0.1f);

        // Act
        PhotogrammetryRockPrimitive reduced = PhotogrammetryRockMeshClusterer.Cluster(
            dense,
            cellSizeMeters: 0.24f);

        // Assert
        reduced.Positions.Length.Should().BeLessThan(dense.Positions.Length / 2);
        reduced.Indices.Should().OnlyContain(index => index < reduced.Positions.Length);
        reduced.Indices.Chunk(3).Should().OnlyContain(triangle =>
            triangle[0] != triangle[1] && triangle[1] != triangle[2] && triangle[0] != triangle[2]);
    }

    [Fact]
    public void should_keep_parallel_overhang_layers_separate_when_their_depth_exceeds_the_cluster_cell()
    {
        // Arrange
        var layered = new PhotogrammetryRockPrimitive(
            positions:
            [
                new(0f, 0f, 0f), new(1f, 0f, 0f), new(0f, 1f, 0f),
                new(0f, 0f, 0.4f), new(1f, 0f, 0.4f), new(0f, 1f, 0.4f),
            ],
            normals: Enumerable.Repeat(Vector3.UnitZ, 6).ToArray(),
            texCoords:
            [
                Vector2.Zero, Vector2.UnitX, Vector2.UnitY,
                Vector2.Zero, Vector2.UnitX, Vector2.UnitY,
            ],
            indices: [0, 1, 2, 3, 4, 5],
            baseColorImageBytes: null);

        // Act
        PhotogrammetryRockPrimitive reduced = PhotogrammetryRockMeshClusterer.Cluster(
            layered,
            cellSizeMeters: 0.2f);

        // Assert
        reduced.Positions.Select(position => position.Z).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public void should_not_weld_distant_uv_islands_that_share_the_same_spatial_seam()
    {
        // Arrange
        var uvSeam = new PhotogrammetryRockPrimitive(
            positions:
            [
                new(0f, 0f, 0f), new(1f, 0f, 0f), new(0f, 1f, 0f),
                new(0.02f, 0f, 0f), new(1.02f, 0f, 0f), new(0.02f, 1f, 0f),
            ],
            normals: Enumerable.Repeat(Vector3.UnitZ, 6).ToArray(),
            texCoords:
            [
                new(0.05f, 0.05f), new(0.10f, 0.05f), new(0.05f, 0.10f),
                new(0.75f, 0.75f), new(0.80f, 0.75f), new(0.75f, 0.80f),
            ],
            indices: [0, 1, 2, 3, 4, 5],
            baseColorImageBytes: null);

        // Act
        PhotogrammetryRockPrimitive reduced = PhotogrammetryRockMeshClusterer.Cluster(
            uvSeam,
            cellSizeMeters: 0.6f);

        // Assert
        reduced.Positions.Should().HaveCount(6);
        reduced.Positions.Distinct().Should().HaveCount(3);
    }

    [Fact]
    public void should_keep_clustered_positions_within_one_cell_of_the_measured_scan()
    {
        // Arrange
        PhotogrammetryRockPrimitive dense = CreateGrid(size: 13, spacing: 0.08f);
        const float cellSize = 0.2f;

        // Act
        PhotogrammetryRockPrimitive reduced = PhotogrammetryRockMeshClusterer.Cluster(dense, cellSize);

        // Assert
        reduced.Positions.Should().OnlyContain(position =>
            dense.Positions.Min(source => Vector3.Distance(source, position)) <= cellSize);
    }

    private static PhotogrammetryRockPrimitive CreateGrid(int size, float spacing)
    {
        var positions = new Vector3[size * size];
        var normals = new Vector3[size * size];
        var uvs = new Vector2[size * size];
        for (int row = 0; row < size; row++)
        {
            for (int column = 0; column < size; column++)
            {
                int index = (row * size) + column;
                positions[index] = new Vector3(
                    column * spacing,
                    row * spacing,
                    MathF.Sin(column * 0.3f) * 0.04f);
                normals[index] = Vector3.UnitZ;
                uvs[index] = new Vector2(column / (float)(size - 1), row / (float)(size - 1));
            }
        }

        var indices = new List<uint>();
        for (int row = 0; row < size - 1; row++)
        {
            for (int column = 0; column < size - 1; column++)
            {
                uint topLeft = (uint)((row * size) + column);
                uint topRight = topLeft + 1;
                uint bottomLeft = topLeft + (uint)size;
                uint bottomRight = bottomLeft + 1;
                indices.AddRange([topLeft, bottomLeft, topRight, topRight, bottomLeft, bottomRight]);
            }
        }

        return new PhotogrammetryRockPrimitive(
            positions,
            normals,
            uvs,
            indices.ToArray(),
            baseColorImageBytes: null);
    }
}
