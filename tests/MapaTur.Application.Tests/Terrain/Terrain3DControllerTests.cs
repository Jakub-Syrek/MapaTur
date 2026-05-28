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
}
