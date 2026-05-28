using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class Camera3DTests
{
    [Fact]
    public void Position_AzimuthZeroPitchZero_PlacesCameraOnPositiveXAxisAtDistance()
    {
        var camera = new Camera3D
        {
            Target = Vector3.Zero,
            Distance = 1000f,
            AzimuthRadians = 0f,
            PitchRadians = 0f,
        };

        Vector3 position = camera.Position;

        position.X.Should().BeApproximately(1000f, 1e-3f);
        position.Y.Should().BeApproximately(0f, 1e-3f);
        position.Z.Should().BeApproximately(0f, 1e-3f);
    }

    [Fact]
    public void Position_PitchFortyFive_PlacesCameraAtFortyFiveAboveHorizon()
    {
        var camera = new Camera3D
        {
            Target = Vector3.Zero,
            Distance = 1000f,
            AzimuthRadians = 0f,
            PitchRadians = MathF.PI / 4f,
        };

        Vector3 position = camera.Position;

        float expected = 1000f * MathF.Sqrt(0.5f);
        position.X.Should().BeApproximately(expected, 1e-2f);
        position.Z.Should().BeApproximately(expected, 1e-2f);
    }

    [Fact]
    public void Position_PitchClampedBelowVertical()
    {
        var camera = new Camera3D
        {
            Target = Vector3.Zero,
            Distance = 1000f,
            PitchRadians = MathF.PI,  // 180° — should clamp to just under 90°
        };

        Vector3 position = camera.Position;

        // Pitch clamps so position is not parallel to up axis: x-y plane component must be > 0.
        float horizontal = MathF.Sqrt((position.X * position.X) + (position.Y * position.Y));
        horizontal.Should().BeGreaterThan(1f);
    }

    [Fact]
    public void ProjectToScreen_TargetMapsToScreenCenter()
    {
        var camera = new Camera3D
        {
            Target = new Vector3(100f, 200f, 50f),
            Distance = 1000f,
            AzimuthRadians = 0.7f,
            PitchRadians = 0.5f,
        };

        Vector3? projected = camera.ProjectToScreen(camera.Target, 800f, 600f);

        projected.Should().NotBeNull();
        projected!.Value.X.Should().BeApproximately(400f, 1e-2f);
        projected!.Value.Y.Should().BeApproximately(300f, 1e-2f);
    }

    [Fact]
    public void ProjectToScreen_PointInFrontOfCamera_ReturnsScreenCoordinatesInsideViewport()
    {
        var camera = new Camera3D
        {
            Target = Vector3.Zero,
            Distance = 1000f,
            AzimuthRadians = 0f,
            PitchRadians = MathF.PI / 4f,
            NearPlane = 10f,
            FarPlane = 10000f,
        };

        // A point slightly offset from the target stays close to screen center
        // and must lie within the viewport when the camera is aimed at the origin.
        Vector3? projected = camera.ProjectToScreen(new Vector3(20f, 20f, 0f), 800f, 600f);

        projected.Should().NotBeNull();
        projected!.Value.X.Should().BeInRange(0f, 800f);
        projected!.Value.Y.Should().BeInRange(0f, 600f);
        projected!.Value.Z.Should().BeInRange(0f, 1f);
    }

    [Fact]
    public void ProjectToScreen_PointBehindCamera_ReturnsNull()
    {
        var camera = new Camera3D
        {
            Target = Vector3.Zero,
            Distance = 1000f,
            AzimuthRadians = 0f,        // Camera at +X, looking toward -X.
            PitchRadians = 0f,
            NearPlane = 10f,
            FarPlane = 10000f,
        };

        // Point at +X = 2000 is behind the camera, which is at +X = 1000 looking toward -X.
        Vector3? projected = camera.ProjectToScreen(new Vector3(2000f, 0f, 0f), 800f, 600f);

        projected.Should().BeNull();
    }

    [Fact]
    public void ProjectToScreen_PointBeyondFarPlane_ReturnsNull()
    {
        var camera = new Camera3D
        {
            Target = Vector3.Zero,
            Distance = 1000f,
            AzimuthRadians = 0f,
            PitchRadians = 0f,
            NearPlane = 10f,
            FarPlane = 5000f,
        };

        // Point far beyond the far plane in the view direction.
        Vector3? projected = camera.ProjectToScreen(new Vector3(-10_000f, 0f, 0f), 800f, 600f);

        projected.Should().BeNull();
    }

    [Fact]
    public void BuildViewProjection_EqualsViewTimesProjection()
    {
        var camera = new Camera3D
        {
            Target = new Vector3(50f, 60f, 70f),
            Distance = 1234f,
            AzimuthRadians = 0.42f,
            PitchRadians = 0.31f,
        };
        float aspect = 800f / 600f;
        Matrix4x4 expected = camera.BuildViewMatrix() * camera.BuildProjectionMatrix(aspect);

        Matrix4x4 actual = camera.BuildViewProjection(aspect);

        actual.Should().Be(expected);
    }

    [Fact]
    public void ProjectToScreenWithMatrix_AgreesWithDefaultOverload()
    {
        var camera = new Camera3D
        {
            Target = new Vector3(100f, 200f, 50f),
            Distance = 1500f,
            AzimuthRadians = 0.7f,
            PitchRadians = 0.5f,
            NearPlane = 10f,
            FarPlane = 10000f,
        };
        const float w = 800f, h = 600f;
        Matrix4x4 vp = camera.BuildViewProjection(w / h);
        Vector3 worldPoint = new(120f, 210f, 60f);

        Vector3? viaMatrix = camera.ProjectToScreen(worldPoint, vp, w, h);
        Vector3? viaDefault = camera.ProjectToScreen(worldPoint, w, h);

        viaMatrix.Should().NotBeNull();
        viaDefault.Should().NotBeNull();
        viaMatrix!.Value.X.Should().BeApproximately(viaDefault!.Value.X, 1e-3f);
        viaMatrix!.Value.Y.Should().BeApproximately(viaDefault!.Value.Y, 1e-3f);
        viaMatrix!.Value.Z.Should().BeApproximately(viaDefault!.Value.Z, 1e-3f);
    }

    [Fact]
    public void ProjectToScreenWithMatrix_PointBehindCamera_ReturnsNull()
    {
        var camera = new Camera3D
        {
            Target = Vector3.Zero,
            Distance = 1000f,
            AzimuthRadians = 0f,
            PitchRadians = 0f,
            NearPlane = 10f,
            FarPlane = 10000f,
        };
        Matrix4x4 vp = camera.BuildViewProjection(800f / 600f);

        Vector3? projected = camera.ProjectToScreen(new Vector3(2000f, 0f, 0f), vp, 800f, 600f);

        projected.Should().BeNull();
    }
}