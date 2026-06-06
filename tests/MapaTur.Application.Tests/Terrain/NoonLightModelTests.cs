using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour pinning for <see cref="NoonLightModel"/>: as the sun climbs toward its noon peak
/// the light grows more intense, so high-albedo snow should read as a brighter white instead of
/// the flat grey it shows at lower sun. The model turns the sun direction's vertical component
/// (z, 1 = straight overhead) into a 0..1 "noon factor" and the extra white-lift to apply to snow.
/// The app's sun arc peaks at ~64° (z ≈ 0.90) at noon — see <see cref="Atmosphere"/> — so the ramp
/// is calibrated against that real peak, not an idealised vertical sun.
/// </summary>
public sealed class NoonLightModelTests
{
    private const float Tolerance = 0.001f;

    [Fact]
    public void NoonFactor_BelowRampStart_IsZero()
    {
        NoonLightModel.NoonFactor(0.5f).Should().Be(0f, "low sun gets no midday brightness boost");
    }

    [Fact]
    public void NoonFactor_AtRampStart_IsZero()
    {
        NoonLightModel.NoonFactor(NoonLightModel.RampStart).Should().Be(0f);
    }

    [Fact]
    public void NoonFactor_AtRampFull_IsOne()
    {
        NoonLightModel.NoonFactor(NoonLightModel.RampFull).Should().BeApproximately(1f, Tolerance);
    }

    [Fact]
    public void NoonFactor_AboveRampFull_ClampsToOne()
    {
        NoonLightModel.NoonFactor(1f).Should().Be(1f, "the boost saturates at full once the sun is at its peak");
    }

    [Fact]
    public void NoonFactor_BelowHorizon_IsZero()
    {
        NoonLightModel.NoonFactor(-0.5f).Should().Be(0f, "the night-side sun never brightens snow");
    }

    [Fact]
    public void NoonFactor_AtRampMidpoint_IsHalf()
    {
        float midpoint = (NoonLightModel.RampStart + NoonLightModel.RampFull) / 2f;

        NoonLightModel.NoonFactor(midpoint).Should().BeApproximately(0.5f, Tolerance);
    }

    [Fact]
    public void NoonFactor_IncreasesMonotonicallyAcrossRamp()
    {
        float lower = NoonLightModel.NoonFactor(0.80f);
        float higher = NoonLightModel.NoonFactor(0.86f);

        higher.Should().BeGreaterThan(lower, "more overhead sun means a stronger boost");
    }

    [Fact]
    public void SnowWhiteLift_AtNoonPeak_IsMaxLift()
    {
        NoonLightModel.SnowWhiteLift(NoonLightModel.RampFull)
            .Should().BeApproximately(NoonLightModel.MaxSnowWhiteLift, Tolerance);
    }

    [Fact]
    public void SnowWhiteLift_AtLowSun_IsZero()
    {
        NoonLightModel.SnowWhiteLift(0.5f).Should().Be(0f, "snow keeps its base brightness when the sun is low");
    }

    [Fact]
    public void SnowWhiteLift_NeverExceedsMaxLift()
    {
        NoonLightModel.SnowWhiteLift(1f).Should().BeLessThanOrEqualTo(NoonLightModel.MaxSnowWhiteLift);
    }

    [Fact]
    public void NoonFactor_AtAppMiddaySun_IsNearlyFull()
    {
        // Ties the ramp to the real model: the app's noon sun (Atmosphere at 12:00) must land
        // in the fully-boosted band, otherwise the effect would never fire in-app.
        float middaySunUp = new Atmosphere(timeOfDayHours: 12f).SunDirection.Z;

        NoonLightModel.NoonFactor(middaySunUp).Should().BeGreaterThan(0.9f);
    }
}