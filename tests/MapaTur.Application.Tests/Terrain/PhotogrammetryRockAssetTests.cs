using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class PhotogrammetryRockAssetTests
{
    [Fact]
    public void should_load_gpu_attributes_from_gltf()
    {
        // Arrange
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "hiker.glb");

        // Act
        PhotogrammetryRockAsset asset = PhotogrammetryRockAsset.Load(path);

        // Assert
        asset.Primitives.Should().OnlyContain(
            primitive => primitive.Positions.Length > 0
                && primitive.Positions.Length == primitive.Normals.Length
                && primitive.Positions.Length == primitive.TexCoords.Length
                && primitive.Indices.Length % 3 == 0);
    }

    [Fact]
    public void should_preserve_real_relief_when_fitting_scan_to_wall()
    {
        // Arrange
        var primitive = new PhotogrammetryRockPrimitive(
            positions:
            [
                new Vector3(-1f, -1f, 0f),
                new Vector3(1f, -1f, 0f),
                new Vector3(0f, 1f, 1f),
            ],
            normals: [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
            texCoords: [Vector2.Zero, Vector2.UnitX, Vector2.UnitY],
            indices: [0u, 1u, 2u],
            baseColorImageBytes: null);
        var placement = new RockScanPatchPlacement(
            Center: new Vector3(10f, 20f, 30f),
            OutwardNormal: Vector3.UnitY,
            HeightMeters: 4f);

        // Act
        PhotogrammetryRockPrimitive fitted = RockScanPatchFitter.Fit(primitive, placement);

        // Assert
        Vector3.Dot(fitted.Positions[2] - placement.Center, placement.OutwardNormal)
            .Should().BeApproximately(2f, 0.0001f);
    }

    [Fact]
    public void should_keep_scan_uvs_unchanged_when_fitting()
    {
        // Arrange
        var expectedUvs = new[] { new Vector2(0.1f, 0.2f), new Vector2(0.8f, 0.3f), new Vector2(0.4f, 0.9f) };
        var primitive = new PhotogrammetryRockPrimitive(
            positions:
            [
                new Vector3(-1f, -1f, 0f),
                new Vector3(1f, -1f, 0f),
                new Vector3(0f, 1f, 1f),
            ],
            normals: [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
            texCoords: expectedUvs,
            indices: [0u, 1u, 2u],
            baseColorImageBytes: null);

        // Act
        PhotogrammetryRockPrimitive fitted = RockScanPatchFitter.Fit(
            primitive,
            new RockScanPatchPlacement(Vector3.Zero, Vector3.UnitY, HeightMeters: 8f));

        // Assert
        fitted.TexCoords.Should().Equal(expectedUvs);
    }

    [Fact]
    public void should_limit_outward_relief_independently_from_patch_height()
    {
        // Arrange
        var primitive = new PhotogrammetryRockPrimitive(
            positions:
            [
                new Vector3(-1f, -1f, 0f),
                new Vector3(1f, -1f, 0f),
                new Vector3(0f, 1f, 2f),
            ],
            normals: [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
            texCoords: [Vector2.Zero, Vector2.UnitX, Vector2.UnitY],
            indices: [0u, 1u, 2u],
            baseColorImageBytes: null);
        var placement = new RockScanPatchPlacement(
            Center: Vector3.Zero,
            OutwardNormal: Vector3.UnitY,
            HeightMeters: 18f,
            DepthMeters: 4.5f);

        // Act
        PhotogrammetryRockPrimitive fitted = RockScanPatchFitter.Fit(primitive, placement);

        // Assert
        fitted.Positions.Max(position => Vector3.Dot(position, Vector3.UnitY))
            .Should().BeApproximately(4.5f, 0.0001f);
    }

    [Fact]
    public void should_roll_scan_geometry_inside_the_wall_tangent_plane()
    {
        // Arrange
        var primitive = new PhotogrammetryRockPrimitive(
            positions:
            [
                new Vector3(0f, -1f, 0f),
                new Vector3(0f, 1f, 0f),
                new Vector3(1f, 0f, 0f),
            ],
            normals: [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
            texCoords: [Vector2.Zero, Vector2.UnitY, Vector2.UnitX],
            indices: [0u, 1u, 2u],
            baseColorImageBytes: null);
        var placement = new RockScanPatchPlacement(
            Center: Vector3.Zero,
            OutwardNormal: Vector3.UnitY,
            HeightMeters: 2f,
            DepthMeters: 0f,
            RollRadians: MathF.PI * 0.5f);

        // Act
        PhotogrammetryRockPrimitive fitted = RockScanPatchFitter.Fit(primitive, placement);

        // Assert
        MathF.Abs(fitted.Positions[1].X - fitted.Positions[0].X)
            .Should().BeApproximately(2f, 0.0001f);
    }
}
