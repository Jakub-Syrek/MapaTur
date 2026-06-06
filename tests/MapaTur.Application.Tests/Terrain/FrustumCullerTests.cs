using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class FrustumCullerTests
{
    // Camera at (1000,0,0) looking toward the origin down -X, world Z up, 45° FOV, square aspect.
    private static Matrix4x4 LookAtOriginViewProj()
    {
        var camera = new Camera3D
        {
            Target = Vector3.Zero,
            Distance = 1000f,
            AzimuthRadians = 0f,
            PitchRadians = 0f,
            FieldOfViewYRadians = MathF.PI / 4f,
            NearPlane = 1f,
            FarPlane = 100_000f,
        };
        return camera.BuildViewProjection(1f);
    }

    [Fact]
    public void IsAabbVisible_BoxAtTarget_IsVisible()
    {
        var vp = LookAtOriginViewProj();

        bool visible = FrustumCuller.IsAabbVisible(vp, new Vector3(-100f, -100f, -100f), new Vector3(100f, 100f, 100f));

        visible.Should().BeTrue();
    }

    [Fact]
    public void IsAabbVisible_BoxFarToTheSide_IsCulled()
    {
        var vp = LookAtOriginViewProj();

        // Far out in +Y: well outside the lateral field of view.
        bool visible = FrustumCuller.IsAabbVisible(vp, new Vector3(-100f, 90_000f, -100f), new Vector3(100f, 90_200f, 100f));

        visible.Should().BeFalse();
    }

    [Fact]
    public void IsAabbVisible_BoxBehindCamera_IsCulled()
    {
        var vp = LookAtOriginViewProj();

        // Camera sits at x=1000 looking toward -X; a box further out at +X is entirely behind it.
        bool visible = FrustumCuller.IsAabbVisible(vp, new Vector3(3000f, -100f, -100f), new Vector3(3200f, 100f, 100f));

        visible.Should().BeFalse();
    }

    [Fact]
    public void IsAabbVisible_BoxBeyondFarPlane_IsCulled()
    {
        var vp = LookAtOriginViewProj();

        // Straight ahead but way past the 100 km far plane (camera at x=1000 looking toward -X).
        bool visible = FrustumCuller.IsAabbVisible(vp, new Vector3(-300_000f, -100f, -100f), new Vector3(-299_800f, 100f, 100f));

        visible.Should().BeFalse();
    }

    [Fact]
    public void IsAabbVisible_LargeBoxSpanningFrustum_IsVisible()
    {
        var vp = LookAtOriginViewProj();

        // A huge box enclosing the whole scene must never be culled (no corner-test false negative).
        bool visible = FrustumCuller.IsAabbVisible(vp, new Vector3(-50_000f, -50_000f, -50_000f), new Vector3(50_000f, 50_000f, 50_000f));

        visible.Should().BeTrue();
    }

    [Fact]
    public void IsAabbVisible_MinMaxSwapped_StillHandled()
    {
        var vp = LookAtOriginViewProj();

        // Defensive: callers shouldn't pass swapped bounds, but the test must not depend on ordering
        // for a box that clearly contains the target.
        bool visible = FrustumCuller.IsAabbVisible(vp, new Vector3(100f, 100f, 100f), new Vector3(-100f, -100f, -100f));

        visible.Should().BeTrue();
    }
}