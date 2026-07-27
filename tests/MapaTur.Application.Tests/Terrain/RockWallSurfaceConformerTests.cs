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
