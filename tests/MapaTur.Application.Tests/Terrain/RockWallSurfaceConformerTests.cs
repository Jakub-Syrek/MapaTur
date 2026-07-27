using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class RockWallSurfaceConformerTests
{
    [Fact]
    public void should_weld_scan_relief_to_wall_at_patch_edge()
    {
        // Arrange
        PhotogrammetryRockPrimitive fitted = CreateFittedPatch();
        RockWallSurfaceSampler wall = CreateWall(y: 2f);
        var placement = new RockScanPatchPlacement(Vector3.Zero, Vector3.UnitY, HeightMeters: 2f);

        // Act
        PhotogrammetryRockPrimitive conformed = RockWallSurfaceConformer.Conform(
            fitted,
            placement,
            wall,
            edgeBlendFraction: 0.25f);

        // Assert
        conformed.Positions[0].Y.Should().BeApproximately(2f, 0.0001f);
    }

    [Fact]
    public void should_preserve_real_scan_depth_inside_welded_patch()
    {
        // Arrange
        PhotogrammetryRockPrimitive fitted = CreateFittedPatch();
        RockWallSurfaceSampler wall = CreateWall(y: 2f);
        var placement = new RockScanPatchPlacement(Vector3.Zero, Vector3.UnitY, HeightMeters: 2f);

        // Act
        PhotogrammetryRockPrimitive conformed = RockWallSurfaceConformer.Conform(
            fitted,
            placement,
            wall,
            edgeBlendFraction: 0.25f);

        // Assert
        conformed.Positions[4].Y.Should().BeApproximately(3f, 0.0001f);
    }

    [Fact]
    public void should_orient_recalculated_normals_toward_wall_outside()
    {
        // Arrange
        PhotogrammetryRockPrimitive fitted = CreateFittedPatch();

        // Act
        PhotogrammetryRockPrimitive conformed = RockWallSurfaceConformer.Conform(
            fitted,
            new RockScanPatchPlacement(Vector3.Zero, Vector3.UnitY, HeightMeters: 2f),
            CreateWall(y: 2f),
            edgeBlendFraction: 0.25f);

        // Assert
        conformed.Normals.Should().OnlyContain(normal => Vector3.Dot(normal, Vector3.UnitY) >= 0f);
    }

    [Fact]
    public void should_add_clearance_only_inside_patch_while_outline_stays_welded()
    {
        // Arrange
        PhotogrammetryRockPrimitive fitted = CreateFittedPatch();
        RockWallSurfaceSampler wall = CreateWall(y: 2f);
        var placement = new RockScanPatchPlacement(Vector3.Zero, Vector3.UnitY, HeightMeters: 2f);

        // Act
        PhotogrammetryRockPrimitive conformed = RockWallSurfaceConformer.Conform(
            fitted,
            placement,
            wall,
            edgeBlendFraction: 0.25f,
            interiorClearanceMeters: 0.35f);

        // Assert
        conformed.Positions.Select(position => position.Y).Should().Equal(2f, 2f, 2f, 2f, 3.35f);
    }

    [Fact]
    public void should_emit_zero_to_one_seam_weight_from_outline_to_patch_interior()
    {
        // Arrange
        PhotogrammetryRockPrimitive fitted = CreateFittedPatch();
        RockWallSurfaceSampler wall = CreateWall(y: 2f);

        // Act
        PhotogrammetryRockPrimitive conformed = RockWallSurfaceConformer.Conform(
            fitted,
            new RockScanPatchPlacement(Vector3.Zero, Vector3.UnitY, HeightMeters: 2f),
            wall,
            edgeBlendFraction: 0.25f);

        // Assert
        conformed.SeamWeights.Should().Equal(0, 0, 0, 0, 255);
    }

    [Fact]
    public void should_reuse_precomputed_outline_weights_without_changing_the_weld()
    {
        // Arrange
        PhotogrammetryRockPrimitive fitted = CreateFittedPatch();
        byte[] precomputed = RockWallSurfaceConformer.CalculateSourceSeamWeights(
            CreateSourcePatch(),
            edgeBlendFraction: 0.25f);

        // Act
        PhotogrammetryRockPrimitive conformed = RockWallSurfaceConformer.Conform(
            fitted,
            new RockScanPatchPlacement(Vector3.Zero, Vector3.UnitY, HeightMeters: 2f),
            CreateWall(y: 2f),
            edgeBlendFraction: 0.25f,
            precomputedSeamWeights: precomputed);

        // Assert
        conformed.SeamWeights.Should().Equal(0, 0, 0, 0, 255);
    }

    [Fact]
    public void should_weld_concave_scan_outline_not_only_bounding_box()
    {
        // Arrange
        var fitted = new PhotogrammetryRockPrimitive(
            positions:
            [
                new Vector3(-1f, 1f, -1f),
                new Vector3(1f, 1f, -1f),
                new Vector3(1f, 1f, 1f),
                new Vector3(0f, 1f, 0f),
                new Vector3(-1f, 1f, 1f),
            ],
            normals: Enumerable.Repeat(Vector3.UnitY, 5).ToArray(),
            texCoords: Enumerable.Repeat(Vector2.Zero, 5).ToArray(),
            indices: [0, 1, 3, 1, 2, 3, 0, 3, 4],
            baseColorImageBytes: null);
        RockWallSurfaceSampler wall = CreateWall(y: 2f);

        // Act
        PhotogrammetryRockPrimitive conformed = RockWallSurfaceConformer.Conform(
            fitted,
            new RockScanPatchPlacement(Vector3.Zero, Vector3.UnitY, HeightMeters: 2f),
            wall,
            edgeBlendFraction: 0.25f);

        // Assert
        conformed.Positions[3].Y.Should().BeApproximately(2f, 0.0001f);
    }

    [Fact]
    public void should_not_treat_duplicate_uv_seam_vertices_as_patch_outline()
    {
        // Arrange
        Vector3[] positions =
        [
            new(-1f, 1f, -1f), new(1f, 1f, -1f), new(0f, 2f, 0f),
            new(1f, 1f, -1f), new(1f, 1f, 1f), new(0f, 2f, 0f),
            new(1f, 1f, 1f), new(-1f, 1f, 1f), new(0f, 2f, 0f),
            new(-1f, 1f, 1f), new(-1f, 1f, -1f), new(0f, 2f, 0f),
        ];
        var fitted = new PhotogrammetryRockPrimitive(
            positions,
            Enumerable.Repeat(Vector3.UnitY, positions.Length).ToArray(),
            Enumerable.Repeat(Vector2.Zero, positions.Length).ToArray(),
            Enumerable.Range(0, positions.Length).Select(index => (uint)index).ToArray(),
            baseColorImageBytes: null);

        // Act
        PhotogrammetryRockPrimitive conformed = RockWallSurfaceConformer.Conform(
            fitted,
            new RockScanPatchPlacement(Vector3.Zero, Vector3.UnitY, HeightMeters: 2f),
            CreateWall(y: 2f),
            edgeBlendFraction: 0.25f);

        // Assert
        Enumerable.Range(0, 4).Select(index => conformed.SeamWeights[2 + (index * 3)])
            .Should().OnlyContain(weight => weight == byte.MaxValue);
    }

    [Fact]
    public void should_feather_outer_outline_without_erasing_geometry_around_internal_hole()
    {
        // Arrange
        Vector3[] positions =
        [
            new(-2f, 1f, -2f), new(2f, 1f, -2f), new(2f, 1f, 2f), new(-2f, 1f, 2f),
            new(-0.5f, 2f, -0.5f), new(0.5f, 2f, -0.5f), new(0.5f, 2f, 0.5f), new(-0.5f, 2f, 0.5f),
        ];
        uint[] indices =
        [
            0, 1, 5, 0, 5, 4,
            1, 2, 6, 1, 6, 5,
            2, 3, 7, 2, 7, 6,
            3, 0, 4, 3, 4, 7,
        ];
        var fitted = new PhotogrammetryRockPrimitive(
            positions,
            Enumerable.Repeat(Vector3.UnitY, positions.Length).ToArray(),
            Enumerable.Repeat(Vector2.Zero, positions.Length).ToArray(),
            indices,
            baseColorImageBytes: null);

        // Act
        PhotogrammetryRockPrimitive conformed = RockWallSurfaceConformer.Conform(
            fitted,
            new RockScanPatchPlacement(Vector3.Zero, Vector3.UnitY, HeightMeters: 4f),
            CreateWall(y: 2f),
            edgeBlendFraction: 0.25f);

        // Assert
        conformed.SeamWeights.Skip(4).Should().OnlyContain(weight => weight == byte.MaxValue);
    }

    private static PhotogrammetryRockPrimitive CreateFittedPatch() => new(
        positions:
        [
            new Vector3(-1f, 1f, -1f),
            new Vector3(1f, 1f, -1f),
            new Vector3(1f, 1f, 1f),
            new Vector3(-1f, 1f, 1f),
            new Vector3(0f, 1f, 0f),
        ],
        normals: Enumerable.Repeat(Vector3.UnitY, 5).ToArray(),
        texCoords: [Vector2.Zero, Vector2.UnitX, Vector2.One, Vector2.UnitY, new Vector2(0.5f)],
        indices: [0, 1, 4, 1, 2, 4, 2, 3, 4, 3, 0, 4],
        baseColorImageBytes: null);

    private static PhotogrammetryRockPrimitive CreateSourcePatch() => new(
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
        indices: [0, 1, 4, 1, 2, 4, 2, 3, 4, 3, 0, 4],
        baseColorImageBytes: null);

    private static RockWallSurfaceSampler CreateWall(float y)
    {
        var points = new List<Vector3>();
        for (int z = -2; z <= 2; z++)
        {
            for (int x = -2; x <= 2; x++)
            {
                points.Add(new Vector3(x, y, z));
            }
        }

        return new RockWallSurfaceSampler(points, Vector3.UnitY, cellSizeMeters: 1f);
    }
}
