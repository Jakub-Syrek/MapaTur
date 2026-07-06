using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour of <see cref="PerennialFirn"/> — the "lodowczyki" correction, v6 CHANNEL-DOMINANT after the
/// photo ground-truth at Czarny Staw pod Rysami: the real patches are NARROW tongues in the couloir slots
/// the meltwater streams run down, NOT the broad avalanche apron on the cirque floor (which the AO also
/// reads as concave but which melts out in summer). So a mapped stream channel is the primary driver.
/// </summary>
public sealed class PerennialFirnTests
{
    [Fact]
    public void should_hold_a_tongue_along_a_stream_channel_above_the_line()
    {
        // The photo tongues: a couloir with a meltwater stream, N-facing, moderate slope, above the line.
        float w = PerennialFirn.Weight(
            elevationMeters: 2100f, northness: 0.4f, southness: 0f, concavity: 0.3f, slopeCos: 0.85f,
            channel: 1f);

        w.Should().BeGreaterThan(0.6f);
    }

    [Fact]
    public void should_keep_the_broad_open_cirque_floor_bare_at_slider_zero()
    {
        // THE over-coverage fix: the gently-concave avalanche apron (AO ~0.4, no stream) must NOT glaze —
        // it melts in the sun. Only the channels hold firn.
        float w = PerennialFirn.Weight(
            elevationMeters: 2000f, northness: 0.2f, southness: 0f, concavity: 0.45f, slopeCos: 0.9f,
            channel: 0f);

        w.Should().BeApproximately(0f, 0.03f);
    }

    [Fact]
    public void should_keep_a_high_ridge_bare_even_far_above_the_line()
    {
        float w = PerennialFirn.Weight(
            elevationMeters: 2500f, northness: 0f, southness: 0f, concavity: 0f, slopeCos: 0.9f, channel: 0f);

        w.Should().BeApproximately(0f, 0.001f);
    }

    [Fact]
    public void should_keep_an_open_north_slope_bare_no_glaze()
    {
        // A broad OPEN N slope (no channel) stays bare — bare northness is far under the patch threshold.
        float w = PerennialFirn.Weight(
            elevationMeters: 2100f, northness: 0.6f, southness: 0f, concavity: 0.1f, slopeCos: 0.85f,
            channel: 0f);

        w.Should().BeApproximately(0f, 0.02f);
    }

    [Fact]
    public void should_keep_a_sunlit_channel_mostly_bare()
    {
        // Southern (insolated) exposure cancels even a channel — "w miejscu nasłonecznionym nie ma szans".
        float sunlit = PerennialFirn.Weight(
            elevationMeters: 2100f, northness: 0f, southness: 0.8f, concavity: 0.3f, slopeCos: 0.85f, channel: 1f);
        float shaded = PerennialFirn.Weight(
            elevationMeters: 2100f, northness: 0.4f, southness: 0f, concavity: 0.3f, slopeCos: 0.85f, channel: 1f);

        sunlit.Should().BeLessThan(shaded * 0.4f);
    }

    [Fact]
    public void should_run_a_channel_tongue_below_the_open_ground_line()
    {
        // Deposition channels hold snow LOWER (the tongues running toward Czarny Staw): a stream couloir at
        // ~1750 m keeps its tongue while open ground needs ~2000 m.
        float tongue = PerennialFirn.Weight(
            elevationMeters: 1750f, northness: 0.3f, southness: 0f, concavity: 0.3f, slopeCos: 0.85f, channel: 1f);

        tongue.Should().BeGreaterThan(0.4f);
    }

    [Fact]
    public void should_melt_out_below_the_altitude_gate()
    {
        float w = PerennialFirn.Weight(
            elevationMeters: 1_400f, northness: 0.6f, southness: 0f, concavity: 0.3f, slopeCos: 0.9f, channel: 1f);

        w.Should().BeApproximately(0f, 0.001f);
    }

    [Fact]
    public void should_shed_off_a_vertical_headwall()
    {
        float w = PerennialFirn.Weight(
            elevationMeters: 2200f, northness: 0.9f, southness: 0f, concavity: 0.8f, slopeCos: 0.1f, channel: 1f);

        w.Should().BeApproximately(0f, 0.001f);
    }

    [Fact]
    public void should_hold_channel_firn_in_a_steep_couloir()
    {
        // The tongues sit in ~45-50° couloirs, which the open-slope shedding law would bare — packed
        // channel firn is bed-anchored and holds far steeper.
        float couloir = PerennialFirn.Weight(
            elevationMeters: 2100f, northness: 0.4f, southness: 0f, concavity: 0.3f, slopeCos: 0.64f, channel: 1f);

        couloir.Should().BeGreaterThan(0.4f);
    }
}