using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class RockWallCoverageComposerTests
{
    [Fact]
    public void should_merge_real_mesh_instances_and_rebase_triangle_indices()
    {
        // Arrange
        PhotogrammetryRockPrimitive[] variants = [CreateTriangle(), CreateTriangle(), CreateTriangle()];
        RockWallCoveragePatch[] patches =
        [
            CreatePatch(new Vector3(-2f, 0f, 0f), variant: 0),
            CreatePatch(new Vector3(2f, 0f, 0f), variant: 1),
        ];

        // Act
        PhotogrammetryRockPrimitive combined = RockWallCoverageComposer.Compose(
            variants,
            patches,
            CreateWall(),
            edgeBlendFraction: 0.2f,
            interiorClearanceMeters: 0.1f,
            atlasColumns: 3,
            atlasRows: 1,
            atlasBaseColorImageBytes: null);

        // Assert
        combined.Indices.Take(12).Should().OnlyContain(index => index < 5u);
        combined.Indices.Skip(12).Should().OnlyContain(index => index >= 5u);
    }

    [Fact]
    public void should_remap_each_scan_uv_into_its_own_atlas_cell()
    {
        // Arrange
        PhotogrammetryRockPrimitive[] variants = [CreateTriangle(), CreateTriangle(), CreateTriangle()];
        RockWallCoveragePatch[] patches =
        [
            CreatePatch(Vector3.Zero, variant: 1),
        ];

        // Act
        PhotogrammetryRockPrimitive combined = RockWallCoverageComposer.Compose(
            variants,
            patches,
            CreateWall(),
            edgeBlendFraction: 0.2f,
            interiorClearanceMeters: 0.1f,
            atlasColumns: 3,
            atlasRows: 1,
            atlasBaseColorImageBytes: null);

        // Assert
        combined.TexCoords.Should().OnlyContain(uv => uv.X >= (1f / 3f) && uv.X <= (2f / 3f));
    }

    [Fact]
    public void should_keep_every_instance_as_real_outward_geometry_over_the_wall()
    {
        // Arrange
        PhotogrammetryRockPrimitive[] variants = [CreateTriangle(), CreateTriangle(), CreateTriangle()];
        RockWallCoveragePatch[] patches =
        [
            CreatePatch(new Vector3(-2f, 0f, 0f), variant: 0),
            CreatePatch(new Vector3(2f, 0f, 0f), variant: 2),
        ];

        // Act
        PhotogrammetryRockPrimitive combined = RockWallCoverageComposer.Compose(
            variants,
            patches,
            CreateWall(),
            edgeBlendFraction: 0.2f,
            interiorClearanceMeters: 0.1f,
            atlasColumns: 3,
            atlasRows: 1,
            atlasBaseColorImageBytes: null);

        // Assert
        combined.Positions.Should().Contain(position => position.Y > 1.5f);
    }

    private static PhotogrammetryRockPrimitive CreateTriangle() => new(
        positions:
        [
            new Vector3(-1f, -1f, 0f),
            new Vector3(1f, -1f, 0f),
            new Vector3(1f, 1f, 0f),
            new Vector3(-1f, 1f, 0f),
            new Vector3(0f, 0f, 1f),
        ],
        normals: Enumerable.Repeat(Vector3.UnitZ, 5).ToArray(),
        texCoords: [Vector2.Zero, Vector2.UnitX, Vector2.One, Vector2.UnitY, new Vector2(0.5f)],
        indices: [0u, 1u, 4u, 1u, 2u, 4u, 2u, 3u, 4u, 3u, 0u, 4u],
        baseColorImageBytes: null);

    private static RockWallCoveragePatch CreatePatch(Vector3 center, int variant) => new(
        new RockScanPatchPlacement(
            center,
            Vector3.UnitY,
            HeightMeters: 2f,
            DepthMeters: 1f),
        variant,
        Column: variant,
        Row: 0,
        WidthMeters: 2f);

    private static RockWallSurfaceSampler CreateWall()
    {
        var points = new List<Vector3>();
        for (int z = -4; z <= 4; z++)
        {
            for (int x = -4; x <= 4; x++)
            {
                points.Add(new Vector3(x, 1f, z));
            }
        }

        return new RockWallSurfaceSampler(points, Vector3.UnitY, cellSizeMeters: 1f);
    }
}
