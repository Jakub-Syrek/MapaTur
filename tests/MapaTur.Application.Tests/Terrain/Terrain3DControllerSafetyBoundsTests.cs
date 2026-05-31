using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class Terrain3DControllerSafetyBoundsTests
{
    private static Terrain3DController BuildController()
    {
        var camera = new Camera3D
        {
            Target = Vector3.Zero,
            Distance = 10_000f,
            AzimuthRadians = 0f,
            PitchRadians = MathF.PI / 4f,
        };
        return new Terrain3DController(camera);
    }

    [Fact]
    public void ApplyZoom_PinchInBeyondMinDistance_StopsAtMinDistance()
    {
        var ctrl = BuildController();
        ctrl.MinDistance = 800f;

        for (int i = 0; i < 100; i++)
        {
            ctrl.ApplyZoom(1.2f);
        }

        ctrl.Camera.Distance.Should().Be(800f);
    }

    [Fact]
    public void ApplyVertical_DropsTargetButClampedToMinElevation()
    {
        var ctrl = BuildController();
        ctrl.MinTargetElevation = -500f;

        for (int i = 0; i < 50; i++)
        {
            ctrl.ApplyVertical(-200);
        }

        ctrl.Camera.Target.Z.Should().Be(-500f);
    }

    [Fact]
    public void ApplyPan_OutsideTargetBounds_ClampsToBox()
    {
        var ctrl = BuildController();
        ctrl.MinTargetX = -5_000f;
        ctrl.MaxTargetX = 5_000f;
        ctrl.MinTargetY = -5_000f;
        ctrl.MaxTargetY = 5_000f;

        // 50 big-step pans in the +X direction should NOT push target past +5000.
        for (int i = 0; i < 50; i++)
        {
            ctrl.ApplyPan(500, 0);
        }

        ctrl.Camera.Target.X.Should().BeLessThanOrEqualTo(5_000f);
        ctrl.Camera.Target.X.Should().BeGreaterThanOrEqualTo(-5_000f);
    }

    [Fact]
    public void ApplyPan_DefaultBounds_DoesNotClamp()
    {
        var ctrl = BuildController();
        // Bounds default to ±Infinity, so a pan should move freely.

        ctrl.ApplyPan(1_000, 0);

        // At azimuth=0 a +X-pixel pan moves Target along world-Y (right vector). Just assert the
        // target moved somewhere far away — proving nothing clamped it back toward origin.
        Vector2 horizontalTarget = new(ctrl.Camera.Target.X, ctrl.Camera.Target.Y);
        horizontalTarget.Length().Should().BeGreaterThan(100f);
    }

    [Fact]
    public void ApplyOrbit_DoesNotJump_PitchOnlyChangesAsRequested()
    {
        // Regression guard against the earlier EnforceCameraFloor implementation that lifted the
        // distance whenever a small orbit dropped Camera.Z under a floor — felt like the camera
        // was juddering. With no floor, an orbit must only rotate, never translate.
        var ctrl = BuildController();
        float distanceBefore = ctrl.Camera.Distance;

        ctrl.ApplyOrbit(0, -100);

        ctrl.Camera.Distance.Should().Be(distanceBefore);
    }
}