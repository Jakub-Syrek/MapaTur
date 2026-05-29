using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class Terrain3DControllerTests
{
    private static Terrain3DController BuildController(out Camera3D camera)
    {
        camera = new Camera3D
        {
            Target = Vector3.Zero,
            Distance = 1000f,
            AzimuthRadians = 0f,
            PitchRadians = MathF.PI / 4f,
        };
        return new Terrain3DController(camera)
        {
            OrbitSensitivity = 0.01f,
            PanSensitivity = 0.001f,
            MinDistance = 100f,
            MaxDistance = 100_000f,
        };
    }

    [Fact]
    public void Constructor_NullCamera_Throws()
    {
        Action act = () => _ = new Terrain3DController(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_ExposesGivenCamera()
    {
        var camera = new Camera3D();
        var ctrl = new Terrain3DController(camera);

        ctrl.Camera.Should().BeSameAs(camera);
    }

    [Fact]
    public void ApplyOrbit_PositiveDx_IncreasesAzimuthByDxTimesSensitivity()
    {
        var ctrl = BuildController(out var camera);

        ctrl.ApplyOrbit(10f, 0f);

        camera.AzimuthRadians.Should().BeApproximately(0.1f, 1e-5f);
    }

    [Fact]
    public void ApplyOrbit_NegativeDx_DecreasesAzimuth()
    {
        var ctrl = BuildController(out var camera);

        ctrl.ApplyOrbit(-5f, 0f);

        camera.AzimuthRadians.Should().BeApproximately(-0.05f, 1e-5f);
    }

    [Fact]
    public void ApplyOrbit_PositiveDy_IncreasesPitchByDyTimesSensitivity()
    {
        var ctrl = BuildController(out var camera);
        float initial = camera.PitchRadians;

        ctrl.ApplyOrbit(0f, 10f);

        camera.PitchRadians.Should().BeApproximately(initial + 0.1f, 1e-5f);
    }

    [Fact]
    public void ApplyOrbit_LargePositiveDy_ClampsPitchBelowVerticalUp()
    {
        var ctrl = BuildController(out var camera);

        ctrl.ApplyOrbit(0f, 100_000f);

        camera.PitchRadians.Should().BeLessThan(MathF.PI / 2f);
    }

    [Fact]
    public void ApplyOrbit_LargeNegativeDy_ClampsPitchAboveVerticalDown()
    {
        var ctrl = BuildController(out var camera);

        ctrl.ApplyOrbit(0f, -100_000f);

        camera.PitchRadians.Should().BeGreaterThan(-MathF.PI / 2f);
    }

    [Fact]
    public void ApplyOrbit_LargeNegativeDy_ClampsPitchToPositiveMinimum()
    {
        var ctrl = BuildController(out var camera);

        ctrl.ApplyOrbit(0f, -100_000f);

        // Input can't tilt the camera to a horizon-grazing or below-ground view, where the
        // painter's algorithm + back-face cull break down. Pitch floors at a positive minimum.
        ctrl.MinPitchRadians.Should().BeGreaterThan(0f);
        camera.PitchRadians.Should().BeApproximately(ctrl.MinPitchRadians, 1e-5f);
    }

    [Fact]
    public void MinPitchRadians_DefaultsToAboutTenDegrees()
    {
        var ctrl = BuildController(out _);

        ctrl.MinPitchRadians.Should().BeApproximately(MathF.PI / 18f, 1e-5f);
    }

    [Fact]
    public void ApplyLookAround_KeepsCameraPositionFixed_WhileChangingAzimuth()
    {
        var ctrl = BuildController(out var camera);
        Vector3 before = camera.Position;
        float beforeAzimuth = camera.AzimuthRadians;

        ctrl.ApplyLookAround(30f, 0f);

        // The camera stays put ("I stand in the middle") — only the view direction rotates.
        camera.Position.X.Should().BeApproximately(before.X, 1e-2f);
        camera.Position.Y.Should().BeApproximately(before.Y, 1e-2f);
        camera.Position.Z.Should().BeApproximately(before.Z, 1e-2f);
        camera.AzimuthRadians.Should().NotBe(beforeAzimuth);
    }

    [Fact]
    public void ApplyLookAround_MovesTheTarget()
    {
        var ctrl = BuildController(out var camera);
        Vector3 targetBefore = camera.Target;

        ctrl.ApplyLookAround(30f, 0f);

        camera.Target.Should().NotBe(targetBefore);
    }

    [Fact]
    public void ApplyZoom_ScaleGreaterThanOne_DividesDistance()
    {
        var ctrl = BuildController(out var camera);

        ctrl.ApplyZoom(2f);

        camera.Distance.Should().BeApproximately(500f, 1e-3f);
    }

    [Fact]
    public void ApplyZoom_ScaleLessThanOne_MultipliesDistance()
    {
        var ctrl = BuildController(out var camera);

        ctrl.ApplyZoom(0.5f);

        camera.Distance.Should().BeApproximately(2000f, 1e-3f);
    }

    [Fact]
    public void ApplyZoom_LargeScale_ClampsDistanceToMinimum()
    {
        var ctrl = BuildController(out var camera);

        ctrl.ApplyZoom(10_000f);

        camera.Distance.Should().Be(100f);
    }

    [Fact]
    public void ApplyZoom_TinyScale_ClampsDistanceToMaximum()
    {
        var ctrl = BuildController(out var camera);

        ctrl.ApplyZoom(0.0001f);

        camera.Distance.Should().Be(100_000f);
    }

    [Fact]
    public void ApplyZoom_NonPositiveScale_IsNoop()
    {
        var ctrl = BuildController(out var camera);
        float initial = camera.Distance;

        ctrl.ApplyZoom(0f);

        camera.Distance.Should().Be(initial);
    }

    [Fact]
    public void ApplyPan_AtZeroAzimuth_PositiveDxIncreasesTargetY()
    {
        var ctrl = BuildController(out var camera);

        ctrl.ApplyPan(10f, 0f);

        camera.Target.Y.Should().BeGreaterThan(0f);
    }

    [Fact]
    public void ApplyPan_AtZeroAzimuth_PositiveDxLeavesTargetXUnchanged()
    {
        var ctrl = BuildController(out var camera);

        ctrl.ApplyPan(10f, 0f);

        camera.Target.X.Should().BeApproximately(0f, 1e-4f);
    }

    [Fact]
    public void ApplyPan_AtZeroAzimuth_PositiveDyDecreasesTargetX()
    {
        var ctrl = BuildController(out var camera);

        ctrl.ApplyPan(0f, 10f);

        camera.Target.X.Should().BeLessThan(0f);
    }

    [Fact]
    public void ApplyPan_AtZeroAzimuth_PositiveDyLeavesTargetYUnchanged()
    {
        var ctrl = BuildController(out var camera);

        ctrl.ApplyPan(0f, 10f);

        camera.Target.Y.Should().BeApproximately(0f, 1e-4f);
    }

    [Fact]
    public void ApplyPan_PanMagnitudeScalesWithDistance()
    {
        var ctrlNear = BuildController(out var camNear);
        camNear.Distance = 1000f;
        ctrlNear.ApplyPan(10f, 0f);

        var ctrlFar = BuildController(out var camFar);
        camFar.Distance = 2000f;
        ctrlFar.ApplyPan(10f, 0f);

        camFar.Target.Y.Should().BeApproximately(camNear.Target.Y * 2f, 1e-3f);
    }

    [Fact]
    public void ApplyPan_AtNinetyDegreeAzimuth_PositiveDxDecreasesTargetX()
    {
        var ctrl = BuildController(out var camera);
        camera.AzimuthRadians = MathF.PI / 2f;

        ctrl.ApplyPan(10f, 0f);

        camera.Target.X.Should().BeLessThan(0f);
    }

    [Fact]
    public void ApplyVertical_PositivePixels_RaisesTargetZ()
    {
        var ctrl = BuildController(out var camera);
        float before = camera.Target.Z;

        ctrl.ApplyVertical(10f);

        camera.Target.Z.Should().BeGreaterThan(before);
    }

    [Fact]
    public void ApplyVertical_NegativePixels_LowersTargetZ()
    {
        var ctrl = BuildController(out var camera);
        float before = camera.Target.Z;

        ctrl.ApplyVertical(-10f);

        camera.Target.Z.Should().BeLessThan(before);
    }

    [Fact]
    public void ApplyVertical_StepMagnitudeScalesWithDistance()
    {
        var ctrlNear = BuildController(out var camNear);
        camNear.Distance = 1000f;
        ctrlNear.ApplyVertical(10f);

        var ctrlFar = BuildController(out var camFar);
        camFar.Distance = 4000f;
        ctrlFar.ApplyVertical(10f);

        camFar.Target.Z.Should().BeApproximately(camNear.Target.Z * 4f, 1e-3f);
    }

    [Fact]
    public void ApplyVertical_OnlyAffectsZ_NotXOrY()
    {
        var ctrl = BuildController(out var camera);
        camera.Target = new Vector3(1234f, 5678f, 0f);

        ctrl.ApplyVertical(50f);

        camera.Target.X.Should().Be(1234f);
        camera.Target.Y.Should().Be(5678f);
    }
}