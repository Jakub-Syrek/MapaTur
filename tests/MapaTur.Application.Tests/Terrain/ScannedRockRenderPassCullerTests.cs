using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class ScannedRockRenderPassCullerTests
{
    [Fact]
    public void should_keep_visible_page_inside_pass_distance()
    {
        // Arrange
        Camera3D camera = Camera();
        Matrix4x4 viewProjection = camera.BuildViewProjection(16f / 9f);

        // Act
        bool visible = ScannedRockRenderPassCuller.IsVisible(
            viewProjection,
            camera.Position,
            maximumDistanceMeters: 500f,
            worldMin: new Vector3(-1f),
            worldMax: new Vector3(1f));

        // Assert
        visible.Should().BeTrue();
    }

    [Fact]
    public void should_cull_prefetched_page_outside_pass_frustum()
    {
        // Arrange
        Camera3D camera = Camera();
        Matrix4x4 viewProjection = camera.BuildViewProjection(16f / 9f);

        // Act
        bool visible = ScannedRockRenderPassCuller.IsVisible(
            viewProjection,
            camera.Position,
            maximumDistanceMeters: float.PositiveInfinity,
            worldMin: new Vector3(0f, 10_000f, 0f),
            worldMax: new Vector3(10f, 10_010f, 10f));

        // Assert
        visible.Should().BeFalse();
    }

    [Fact]
    public void should_cull_reflection_page_beyond_pass_distance()
    {
        // Arrange
        Camera3D camera = Camera();
        Matrix4x4 viewProjection = camera.BuildViewProjection(16f / 9f);

        // Act
        bool visible = ScannedRockRenderPassCuller.IsVisible(
            viewProjection,
            camera.Position,
            maximumDistanceMeters: 500f,
            worldMin: new Vector3(-1000f, -1f, -1f),
            worldMax: new Vector3(-990f, 1f, 1f));

        // Assert
        visible.Should().BeFalse();
    }

    private static Camera3D Camera() =>
        new()
        {
            Target = Vector3.Zero,
            Distance = 100f,
            AzimuthRadians = 0f,
            PitchRadians = 0f,
            FieldOfViewYRadians = MathF.PI / 3f,
            NearPlane = 1f,
            FarPlane = 100_000f,
        };
}
