using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class ContinuousScannedRockSurfaceBuilderTests
{
    [Fact]
    public void should_build_one_connected_surface_with_relief_bounded_above_the_dem()
    {
        // Arrange
        IReadOnlyList<RockMeshTriangle> wall = CreateWall();

        // Act
        PhotogrammetryRockPrimitive result = ContinuousScannedRockSurfaceBuilder.Build(
            wall,
            static (position, _) => new RockSurfaceSample(
                DisplacementMeters: MathF.Sin(position.Z * 1.7f),
                AmbientOcclusion: 220,
                MaterialVariant: 0),
            sampleAmplitudeMeters: 1f,
            maximumReliefMeters: 2.4f,
            maximumEdgeMeters: 0.7f,
            seed: 1234,
            baseColorImageBytes: null);

        // Assert
        result.Positions.Should().OnlyContain(position =>
            position.X >= -0.0001f && position.X <= 2.4001f);
    }

    [Fact]
    public void should_break_the_regular_midpoint_lattice_with_bounded_tangent_jitter()
    {
        // Arrange
        IReadOnlyList<RockMeshTriangle> wall = CreateWall();

        // Act
        PhotogrammetryRockPrimitive result = ContinuousScannedRockSurfaceBuilder.Build(
            wall,
            static (_, _) => new RockSurfaceSample(0f, 255, 0),
            sampleAmplitudeMeters: 1f,
            maximumReliefMeters: 1f,
            maximumEdgeMeters: 0.7f,
            seed: 5678,
            baseColorImageBytes: null);

        // Assert
        result.Positions
            .Select(position => MathF.Round(position.Y * 1000f) % 500f)
            .Distinct()
            .Should()
            .HaveCountGreaterThan(2);
    }

    [Fact]
    public void should_cap_tangent_jitter_independently_of_the_subdivision_edge_limit()
    {
        // Arrange
        IReadOnlyList<RockMeshTriangle> wall = CreateWall();
        Vector3[] originalVertices = wall
            .SelectMany(triangle => new[] { triangle.A, triangle.B, triangle.C })
            .Distinct()
            .ToArray();

        // Act
        PhotogrammetryRockPrimitive result = ContinuousScannedRockSurfaceBuilder.Build(
            wall,
            static (_, _) => new RockSurfaceSample(-1f, 255, 0),
            sampleAmplitudeMeters: 1f,
            maximumReliefMeters: 1f,
            maximumEdgeMeters: 20f,
            seed: 5678,
            baseColorImageBytes: null);

        // Assert
        result.Positions.Should().OnlyContain(position =>
            originalVertices.Min(original => Vector3.Distance(original, position)) <= 0.3f);
    }

    [Fact]
    public void should_produce_uvs_and_smooth_normals_for_every_shared_vertex()
    {
        // Arrange
        IReadOnlyList<RockMeshTriangle> wall = CreateWall();

        // Act
        PhotogrammetryRockPrimitive result = ContinuousScannedRockSurfaceBuilder.Build(
            wall,
            static (position, _) => new RockSurfaceSample(position.Y * 0.1f, 255, 0),
            sampleAmplitudeMeters: 1f,
            maximumReliefMeters: 1.5f,
            maximumEdgeMeters: 0.7f,
            seed: 9012,
            baseColorImageBytes: null);

        // Assert
        result.Positions.Length.Should().Be(result.TexCoords.Length);
        result.Normals.Should().OnlyContain(normal =>
            float.IsFinite(normal.X)
            && float.IsFinite(normal.Y)
            && float.IsFinite(normal.Z)
            && MathF.Abs(normal.Length() - 1f) < 0.001f);
    }

    [Fact]
    public void should_keep_neutral_scan_values_close_to_the_dem_instead_of_floating_halfway_out()
    {
        // Arrange
        IReadOnlyList<RockMeshTriangle> wall = CreateWall();

        // Act
        PhotogrammetryRockPrimitive result = ContinuousScannedRockSurfaceBuilder.Build(
            wall,
            static (_, _) => new RockSurfaceSample(0f, 255, 0),
            sampleAmplitudeMeters: 1f,
            maximumReliefMeters: 2.4f,
            maximumEdgeMeters: 0.7f,
            seed: 7341,
            baseColorImageBytes: null);

        // Assert
        result.Positions.Max(position => position.X).Should().BeLessThan(0.4f);
    }

    [Fact]
    public void should_fade_the_shell_from_its_real_mesh_boundary_without_rectangular_cut_lines()
    {
        // Arrange
        IReadOnlyList<RockMeshTriangle> wall = CreateGridWall(size: 8);

        // Act
        PhotogrammetryRockPrimitive result = ContinuousScannedRockSurfaceBuilder.Build(
            wall,
            static (_, _) => new RockSurfaceSample(0f, 255, 0),
            sampleAmplitudeMeters: 1f,
            maximumReliefMeters: 1f,
            maximumEdgeMeters: 1.5f,
            seed: 983,
            baseColorImageBytes: null);

        // Assert
        result.SeamWeights.Should().Contain(0).And.Contain(value => value > 240);
    }

    [Fact]
    public void should_keep_shallow_dem_triangles_as_an_invisible_bridge_between_rock_faces()
    {
        // Arrange
        IReadOnlyList<RockMeshTriangle> terrain =
        [
            .. CreateWall(),
            new RockMeshTriangle(
                new Vector3(20f, 0f, 0f),
                new Vector3(24f, 0f, 0f),
                new Vector3(20f, 4f, 0f)),
        ];

        // Act
        PhotogrammetryRockPrimitive result = ContinuousScannedRockSurfaceBuilder.Build(
            terrain,
            static (_, _) => new RockSurfaceSample(1f, 255, 0),
            sampleAmplitudeMeters: 1f,
            maximumReliefMeters: 1.5f,
            maximumEdgeMeters: 0.7f,
            seed: 341,
            baseColorImageBytes: null);

        // Assert
        result.Positions.Should().Contain(position =>
            position.X > 19f && MathF.Abs(position.Z) < 0.001f);
    }

    [Fact]
    public void should_only_fade_mesh_boundaries_that_are_on_the_outer_region_edge()
    {
        // Arrange
        IReadOnlyList<RockMeshTriangle> strip = CreateGridWall(size: 8);

        // Act
        PhotogrammetryRockPrimitive result = ContinuousScannedRockSurfaceBuilder.Build(
            strip,
            static (_, _) => new RockSurfaceSample(0f, 255, 0),
            sampleAmplitudeMeters: 1f,
            maximumReliefMeters: 1f,
            maximumEdgeMeters: 1.5f,
            seed: 612,
            baseColorImageBytes: null,
            fadeBoundaryVertex: position => position.Y < 0.01f);

        // Assert
        result.Positions
            .Select((position, index) => (position, weight: result.SeamWeights[index]))
            .Where(vertex => vertex.position.Y > 7.5f)
            .Should()
            .OnlyContain(vertex => vertex.weight > 240);
    }

    [Fact]
    public void should_keep_non_rock_vertices_on_dem_in_hybrid_surface()
    {
        // Arrange
        IReadOnlyList<RockMeshTriangle> terrain =
        [
            .. CreateWall(),
            new RockMeshTriangle(
                new Vector3(20f, 0f, 0f),
                new Vector3(24f, 0f, 0f),
                new Vector3(20f, 4f, 0f)),
        ];

        // Act
        HybridTerrainMesh result = ContinuousScannedRockSurfaceBuilder.BuildHybrid(
            terrain,
            static (_, _) => new RockSurfaceSample(1f, 180, 7),
            sampleAmplitudeMeters: 1f,
            maximumReliefMeters: 2.8f,
            maximumEdgeMeters: 20f,
            seed: 341,
            orthoUvForPosition: static _ => new Vector2(0.25f, 0.75f));

        // Assert
        result.Positions
            .Select((position, index) => (position, index))
            .Where(vertex => result.RockBlend[vertex.index] == 0)
            .Should()
            .OnlyContain(vertex =>
                Vector3.Distance(vertex.position, result.LegacyPositions[vertex.index]) < 1e-6f);
    }

    private static IReadOnlyList<RockMeshTriangle> CreateWall()
    {
        var a = new Vector3(0f, 0f, 0f);
        var b = new Vector3(0f, 4f, 0f);
        var c = new Vector3(0f, 0f, 4f);
        var d = new Vector3(0f, 4f, 4f);
        return
        [
            new RockMeshTriangle(a, b, c),
            new RockMeshTriangle(b, d, c),
        ];
    }

    private static List<RockMeshTriangle> CreateGridWall(int size)
    {
        var result = new List<RockMeshTriangle>(size * size * 2);
        for (int y = 0; y < size; y++)
        {
            for (int z = 0; z < size; z++)
            {
                var a = new Vector3(0f, y, z);
                var b = new Vector3(0f, y + 1, z);
                var c = new Vector3(0f, y, z + 1);
                var d = new Vector3(0f, y + 1, z + 1);
                result.Add(new RockMeshTriangle(a, b, c));
                result.Add(new RockMeshTriangle(b, d, c));
            }
        }

        return result;
    }
}
