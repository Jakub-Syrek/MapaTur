using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour pinning for <see cref="SunScreenProjection"/>: where the directional sun lands in the
/// post-process texture's UV space (origin bottom-left, matching the fullscreen post passes) and when the
/// screen-space god-ray pass should draw at all. The orbit camera at azimuth 0, pitch 0 sits on +X and
/// looks toward −X, so a sun pointing −X is dead ahead.
/// </summary>
public sealed class SunScreenProjectionTests
{
    private static Camera3D LookingTowardNegativeX() => new()
    {
        Target = Vector3.Zero,
        Distance = 1000f,
        AzimuthRadians = 0f,
        PitchRadians = 0f,
        FieldOfViewYRadians = MathF.PI / 4f,
    };

    [Fact]
    public void Project_SunDeadAhead_IsVisibleNearScreenCentre()
    {
        var camera = LookingTowardNegativeX();

        (bool visible, Vector2 uv) = SunScreenProjection.Project(camera, new Vector3(-1f, 0f, 0f), 1600f, 900f);

        visible.Should().BeTrue("a sun aligned with the view direction is on screen");
        uv.X.Should().BeApproximately(0.5f, 0.05f);
        uv.Y.Should().BeApproximately(0.5f, 0.05f);
    }

    [Fact]
    public void Project_SunBehindCamera_IsNotVisible()
    {
        var camera = LookingTowardNegativeX();

        // Camera looks toward −X; a sun toward +X is behind it (no rays).
        (bool visible, _) = SunScreenProjection.Project(camera, new Vector3(1f, 0f, 0f), 1600f, 900f);

        visible.Should().BeFalse();
    }

    [Fact]
    public void Project_SunInFrontButFarToTheSide_IsNotVisible()
    {
        var camera = LookingTowardNegativeX();

        // Forward (−X) component keeps it in front, but the strong +Y throws it well off the frame edge.
        (bool visible, _) = SunScreenProjection.Project(camera, new Vector3(-1f, 6f, 0f), 1600f, 900f);

        visible.Should().BeFalse();
    }

    [Fact]
    public void Project_NonPositiveViewport_IsNotVisible()
    {
        var camera = LookingTowardNegativeX();

        SunScreenProjection.Project(camera, new Vector3(-1f, 0f, 0f), 0f, 900f).Visible.Should().BeFalse();
    }

    [Fact]
    public void Project_ZeroSunDirection_IsNotVisible()
    {
        var camera = LookingTowardNegativeX();

        SunScreenProjection.Project(camera, Vector3.Zero, 1600f, 900f).Visible.Should().BeFalse();
    }
}