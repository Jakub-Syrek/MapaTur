using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// The "2D map" mode policy: climbing past the altitude ceiling morphs the 3D view into a top-down
/// hypsometric map (camera to nadir, ortho faded out) for fast repositioning; descending restores the
/// exact pitch/azimuth the user had when they entered, at the new location. Pure state machine —
/// the view feeds altitude + dt, the renderer consumes the blend.
/// </summary>
public sealed class TopDownMapModeTests
{
    private static TopDownMapMode NewMode() => new()
    {
        EnterAltitudeMeters = 7200.0,
        ExitAltitudeMeters = 6500.0,
        TransitionSeconds = 0.5,
    };

    [Fact]
    public void should_start_inactive_with_zero_blend()
    {
        var mode = NewMode();

        mode.IsActive.Should().BeFalse();
        mode.Blend.Should().Be(0f);
    }

    [Fact]
    public void should_activate_and_save_the_view_when_climbing_past_the_enter_altitude()
    {
        var mode = NewMode();

        mode.Update(eyeAltitudeMeters: 7300.0, dtSeconds: 0.1, currentPitchRadians: 0.7f, currentAzimuthRadians: 1.2f);

        mode.IsActive.Should().BeTrue();
        mode.SavedPitchRadians.Should().Be(0.7f);
        mode.SavedAzimuthRadians.Should().Be(1.2f);
    }

    [Fact]
    public void should_ramp_the_blend_towards_one_over_the_transition_time()
    {
        var mode = NewMode();

        mode.Update(7300.0, 0.25, 0.7f, 1.2f);
        float halfway = mode.Blend;
        mode.Update(7300.0, 0.25, 0.7f, 1.2f);

        halfway.Should().BeApproximately(0.5f, 0.01f);
        mode.Blend.Should().BeApproximately(1f, 0.01f);
    }

    [Fact]
    public void should_clamp_the_blend_to_one_for_a_huge_frame_delta()
    {
        var mode = NewMode();

        mode.Update(7300.0, 10.0, 0.7f, 1.2f);

        mode.Blend.Should().Be(1f);
    }

    [Fact]
    public void should_hold_the_state_in_the_hysteresis_band()
    {
        var mode = NewMode();

        mode.Update(7000.0, 1.0, 0.7f, 1.2f); // below enter, above exit — never entered
        bool stillOff = mode.IsActive;
        mode.Update(7300.0, 1.0, 0.7f, 1.2f); // enter
        mode.Update(7000.0, 1.0, 0.3f, 0.4f); // back into the band — must STAY on

        stillOff.Should().BeFalse();
        mode.IsActive.Should().BeTrue("the hysteresis band must not flap the mode");
    }

    [Fact]
    public void should_keep_the_first_saved_view_while_active()
    {
        var mode = NewMode();

        mode.Update(7300.0, 1.0, 0.7f, 1.2f);
        mode.Update(7400.0, 1.0, 1.4f, 2.6f); // camera pitched by the mode itself — must not overwrite

        mode.SavedPitchRadians.Should().Be(0.7f);
        mode.SavedAzimuthRadians.Should().Be(1.2f);
    }

    [Fact]
    public void should_deactivate_and_ramp_back_when_descending_below_the_exit_altitude()
    {
        var mode = NewMode();
        mode.Update(7300.0, 10.0, 0.7f, 1.2f); // fully in

        mode.Update(6400.0, 0.25, 1.5f, 1.2f);

        mode.IsActive.Should().BeFalse();
        mode.Blend.Should().BeApproximately(0.5f, 0.01f);
    }

    [Fact]
    public void should_save_the_view_again_on_a_fresh_entry()
    {
        var mode = NewMode();
        mode.Update(7300.0, 10.0, 0.7f, 1.2f);
        mode.Update(6400.0, 10.0, 1.5f, 1.2f); // out

        mode.Update(7300.0, 1.0, 0.9f, 2.0f);  // in again, from a different view

        mode.SavedPitchRadians.Should().Be(0.9f);
        mode.SavedAzimuthRadians.Should().Be(2.0f);
    }
}