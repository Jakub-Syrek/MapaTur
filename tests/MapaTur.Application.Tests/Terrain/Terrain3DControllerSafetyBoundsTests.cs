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
    public void ApplyPan_WithOrbitOffsetLargerThanFootprint_KeepsEyeOverMapAndReachesBothEdges()
    {
        // The eye sits ~7 km from the target (distance 10 km, 45°); the footprint is only ±2 km — SMALLER
        // than that offset. Clamping the TARGET to the box would leave the EYE 5-9 km outside it ("the lock
        // won't let me get the camera over the map"). The eye-clamp must keep the EYE inside for every pan,
        // AND let it traverse to both edges.
        var ctrl = BuildController();
        ctrl.Camera.AzimuthRadians = MathF.PI / 2f; // offset lies along +Y, the axis we pan
        ctrl.MinTargetX = -2_000f;
        ctrl.MaxTargetX = 2_000f;
        ctrl.MinTargetY = -2_000f;
        ctrl.MaxTargetY = 2_000f;

        float minEyeY = float.PositiveInfinity;
        float maxEyeY = float.NegativeInfinity;
        for (int i = 0; i < 50; i++)
        {
            ctrl.ApplyPan(0, 400);
            Vector3 p = ctrl.Camera.Position;
            p.X.Should().BeInRange(-2_001f, 2_001f);
            p.Y.Should().BeInRange(-2_001f, 2_001f);
            minEyeY = MathF.Min(minEyeY, p.Y);
            maxEyeY = MathF.Max(maxEyeY, p.Y);
        }

        maxEyeY.Should().BeGreaterThan(1_500f, "the eye must reach the far edge of the map");
        minEyeY.Should().BeLessThan(-1_500f, "the eye must reach the near edge of the map");
    }

    [Fact]
    public void ClampToBounds_EyeStartsOffMap_PullsEyeBackOverFootprint()
    {
        var ctrl = BuildController(); // az 0, 45° → eye ≈ (7070, 0, 7070), well outside a ±1 km box
        ctrl.MinTargetX = -1_000f;
        ctrl.MaxTargetX = 1_000f;
        ctrl.MinTargetY = -1_000f;
        ctrl.MaxTargetY = 1_000f;

        ctrl.ClampToBounds();

        Vector3 p = ctrl.Camera.Position;
        p.X.Should().BeInRange(-1_001f, 1_001f);
        p.Y.Should().BeInRange(-1_001f, 1_001f);
    }

    [Fact]
    public void ApplyVertical_CannotSinkTheEyeBelowTheFloor()
    {
        // "Wys. ▼ can still put the camera under the map." Lowering the look-point must not drop the EYE
        // below the floor (set 100 m above the terrain in the app).
        var ctrl = BuildController(); // distance 10k, 45° → eye.Z ≈ 7071
        ctrl.CameraFloorZ = 5_000f;

        for (int i = 0; i < 100; i++)
        {
            ctrl.ApplyVertical(-500);
        }

        ctrl.Camera.Position.Z.Should().BeGreaterThanOrEqualTo(5_000f - 1f);
    }

    [Fact]
    public void ApplyZoom_PinchInCannotSinkTheEyeBelowTheFloor()
    {
        var ctrl = BuildController();
        ctrl.CameraFloorZ = 6_000f;

        for (int i = 0; i < 100; i++)
        {
            ctrl.ApplyZoom(1.2f);
        }

        ctrl.Camera.Position.Z.Should().BeGreaterThanOrEqualTo(6_000f - 1f);
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