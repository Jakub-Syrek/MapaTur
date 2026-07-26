using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class RockMeshSubdividerTests
{
    [Fact]
    public void should_subdivide_until_every_edge_fits_the_lod_target()
    {
        // Arrange
        var source = new[]
        {
            new RockMeshTriangle(Vector3.Zero, new Vector3(4f, 0f, 0f), new Vector3(0f, 4f, 0f)),
        };

        // Act
        IReadOnlyList<RockMeshTriangle> result = RockMeshSubdivider.Subdivide(source, 1f);

        // Assert
        result.SelectMany(t => t.EdgeLengths).Max().Should().BeLessThanOrEqualTo(1.00001f);
    }

    [Fact]
    public void should_reuse_the_exact_midpoint_on_a_shared_source_edge()
    {
        // Arrange
        Vector3 a = Vector3.Zero;
        Vector3 b = new(2f, 0f, 0f);
        var source = new[]
        {
            new RockMeshTriangle(a, b, new Vector3(0f, 2f, 0f)),
            new RockMeshTriangle(b, a, new Vector3(2f, -2f, 0f)),
        };

        // Act
        IReadOnlyList<RockMeshTriangle> result = RockMeshSubdivider.Subdivide(source, 1f);
        Vector3 sharedMidpoint = (a + b) * 0.5f;

        // Assert
        result.SelectMany(t => t.Vertices).Count(v => v == sharedMidpoint).Should().BeGreaterThan(1);
    }

    [Fact]
    public void should_measure_vertical_wall_as_ninety_degree_slope()
    {
        // Arrange
        var wall = new RockMeshTriangle(
            Vector3.Zero,
            new Vector3(0f, 2f, 0f),
            new Vector3(0f, 0f, 2f));

        // Act
        float slope = wall.SlopeDegrees;

        // Assert
        slope.Should().BeApproximately(90f, 0.001f);
    }

    [Fact]
    public void should_not_over_refine_short_edges_because_one_edge_is_very_long()
    {
        // Arrange
        var source = new[]
        {
            new RockMeshTriangle(
                Vector3.Zero,
                new Vector3(64f, 0f, 0f),
                new Vector3(0f, 0.25f, 0f)),
        };

        // Act
        IReadOnlyList<RockMeshTriangle> result = RockMeshSubdivider.Subdivide(source, 0.25f);

        // Assert
        result.Count.Should().BeLessThan(25_000);
    }
}
