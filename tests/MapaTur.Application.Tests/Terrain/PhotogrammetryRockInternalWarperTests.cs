using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class PhotogrammetryRockInternalWarperTests
{
    [Fact]
    public void should_produce_deterministic_but_internally_different_geometry_per_seed()
    {
        // Arrange
        PhotogrammetryRockPrimitive source = CreateGrid();

        // Act
        PhotogrammetryRockPrimitive first = PhotogrammetryRockInternalWarper.Warp(source, seed: 101);
        PhotogrammetryRockPrimitive repeated = PhotogrammetryRockInternalWarper.Warp(source, seed: 101);
        PhotogrammetryRockPrimitive second = PhotogrammetryRockInternalWarper.Warp(source, seed: 202);

        // Assert
        repeated.Positions.Should().Equal(first.Positions);
        second.Positions.Should().NotEqual(first.Positions);
    }

    [Fact]
    public void should_keep_the_outer_frame_stable_for_dem_welding()
    {
        // Arrange
        PhotogrammetryRockPrimitive source = CreateGrid();

        // Act
        PhotogrammetryRockPrimitive warped = PhotogrammetryRockInternalWarper.Warp(source, seed: 7);

        // Assert
        Enumerable.Range(0, source.Positions.Length)
            .Where(index =>
                source.Positions[index].X is 0f or 4f
                || source.Positions[index].Y is 0f or 4f)
            .Should().OnlyContain(index => warped.Positions[index] == source.Positions[index]);
    }

    [Fact]
    public void should_preserve_topology_texture_and_measured_depth_character()
    {
        // Arrange
        PhotogrammetryRockPrimitive source = CreateGrid();
        float sourceDepth = source.Positions.Max(position => position.Z)
            - source.Positions.Min(position => position.Z);

        // Act
        PhotogrammetryRockPrimitive warped = PhotogrammetryRockInternalWarper.Warp(source, seed: 42);

        // Assert
        warped.Indices.Should().Equal(source.Indices);
        warped.TexCoords.Should().Equal(source.TexCoords);
        warped.BaseColorImageBytes.Should().BeSameAs(source.BaseColorImageBytes);
        (warped.Positions.Max(position => position.Z) - warped.Positions.Min(position => position.Z))
            .Should().BeGreaterThan(sourceDepth * 0.75f);
    }

    private static PhotogrammetryRockPrimitive CreateGrid()
    {
        const int size = 5;
        var positions = new Vector3[size * size];
        var texCoords = new Vector2[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int index = (y * size) + x;
                positions[index] = new Vector3(x, y, MathF.Sin((x * 0.8f) + (y * 0.37f)));
                texCoords[index] = new Vector2(x / 4f, y / 4f);
            }
        }

        var indices = new List<uint>();
        for (int y = 0; y < size - 1; y++)
        {
            for (int x = 0; x < size - 1; x++)
            {
                uint a = (uint)((y * size) + x);
                uint b = a + 1;
                uint c = a + size;
                uint d = c + 1;
                indices.AddRange([a, c, b, b, c, d]);
            }
        }

        return new PhotogrammetryRockPrimitive(
            positions,
            Enumerable.Repeat(Vector3.UnitZ, positions.Length).ToArray(),
            texCoords,
            indices.ToArray(),
            baseColorImageBytes: [1, 2, 3, 4]);
    }
}
