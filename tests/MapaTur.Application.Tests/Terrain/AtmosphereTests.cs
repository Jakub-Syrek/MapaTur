using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour pinning for the <see cref="Atmosphere"/> model: solar geometry over the day,
/// preset sky/sun/ambient colours at the four canonical times-of-day (sunrise, noon, sunset,
/// night), and the fog-density curve that drives aerial perspective. The Tatra location
/// (lat ~49° N) and a fixed solstice date are baked into the geometry — same renderer
/// produces a deterministic sun arc every run.
/// </summary>
public sealed class AtmosphereTests
{
    private const float Tolerance = 0.01f;

    [Fact]
    public void SunDirection_AtNoon_PointsUpAndSlightlySouth()
    {
        var atmo = new Atmosphere(timeOfDayHours: 12f);

        atmo.SunDirection.Z.Should().BeGreaterThan(0.85f, "the Tatra midday sun is high in the sky");
        atmo.SunDirection.Y.Should().BeLessThan(0f, "the sun sits to the south, our +Y is north");
    }

    [Fact]
    public void SnowAmount_DefaultsToZero()
    {
        new Atmosphere(timeOfDayHours: 12f).SnowAmount.Should().Be(0f);
    }

    [Theory]
    [InlineData(-0.5f, 0f)]
    [InlineData(0f, 0f)]
    [InlineData(0.4f, 0.4f)]
    [InlineData(1f, 1f)]
    [InlineData(2f, 1f)]
    public void SnowAmount_IsClampedToUnitRange(float input, float expected)
    {
        new Atmosphere(timeOfDayHours: 12f, snow: input).SnowAmount.Should().Be(expected);
    }

    [Fact]
    public void SunDirection_AtSunrise_IsAtHorizonInTheEast()
    {
        var atmo = new Atmosphere(timeOfDayHours: 6f);

        atmo.SunDirection.X.Should().BeApproximately(1f, 0.05f, "+X is east, sunrise puts the sun there");
        atmo.SunDirection.Z.Should().BeApproximately(0f, 0.05f, "elevation is zero at sunrise");
    }

    [Fact]
    public void SunDirection_AtSunset_IsAtHorizonInTheWest()
    {
        var atmo = new Atmosphere(timeOfDayHours: 18f);

        atmo.SunDirection.X.Should().BeApproximately(-1f, 0.05f, "-X is west, sunset puts the sun there");
        atmo.SunDirection.Z.Should().BeApproximately(0f, 0.05f, "elevation is zero at sunset");
    }

    [Fact]
    public void SunDirection_AtMidnight_IsBelowHorizon()
    {
        var atmo = new Atmosphere(timeOfDayHours: 0f);

        atmo.SunDirection.Z.Should().BeLessThan(0f, "at midnight the sun is below the horizon");
    }

    [Fact]
    public void SunDirection_IsUnitLength()
    {
        // Sun direction must be normalised — the terrain shader feeds it straight into a
        // dot(normal, sun) Lambert without re-normalising, so an unscaled vector would
        // scale lighting by ||sunDir|| instead of producing a [-1,1] cosine.
        var atmo = new Atmosphere(timeOfDayHours: 10.5f);

        atmo.SunDirection.Length().Should().BeApproximately(1f, Tolerance);
    }

    [Fact]
    public void SkyHorizonColor_AtSunset_IsWarmOrange()
    {
        var atmo = new Atmosphere(timeOfDayHours: 18f);

        // Warm tint test: R should clearly dominate B at the horizon during sunset.
        atmo.SkyHorizonColor.X.Should().BeGreaterThan(atmo.SkyHorizonColor.Z + 0.2f);
    }

    [Fact]
    public void SkyZenithColor_AtNoon_IsBlue()
    {
        var atmo = new Atmosphere(timeOfDayHours: 12f);

        // Cool tint test: B should clearly dominate R at the zenith at midday.
        atmo.SkyZenithColor.Z.Should().BeGreaterThan(atmo.SkyZenithColor.X + 0.1f);
    }

    [Fact]
    public void SkyZenithColor_AtMidnight_IsNearBlack()
    {
        var atmo = new Atmosphere(timeOfDayHours: 0f);

        // All channels low: no ambient sunlight scatter at night.
        float brightness = (atmo.SkyZenithColor.X + atmo.SkyZenithColor.Y + atmo.SkyZenithColor.Z) / 3f;
        brightness.Should().BeLessThan(0.1f);
    }

    [Fact]
    public void AmbientFactor_AtNoon_IsHigherThanAtSunset()
    {
        var noon = new Atmosphere(timeOfDayHours: 12f);
        var sunset = new Atmosphere(timeOfDayHours: 18f);

        noon.AmbientFactor.Should().BeGreaterThan(sunset.AmbientFactor);
    }

    [Fact]
    public void AmbientFactor_AtMidnight_IsLowest()
    {
        var midnight = new Atmosphere(timeOfDayHours: 0f);
        var sunset = new Atmosphere(timeOfDayHours: 18f);

        midnight.AmbientFactor.Should().BeLessThan(sunset.AmbientFactor);
    }

    [Fact]
    public void FogDensity_AtSunriseAndSunset_IsHigherThanAtNoon()
    {
        // Aerial perspective is strongest at low sun angles (longer atmospheric path) — the
        // distant-ridge haze that gives "golden hour" photos their depth. The model bumps
        // density when the sun sits near the horizon.
        var noon = new Atmosphere(timeOfDayHours: 12f);
        var sunset = new Atmosphere(timeOfDayHours: 18f);

        sunset.FogDensity.Should().BeGreaterThan(noon.FogDensity);
    }

    [Fact]
    public void SunColor_AtSunset_IsWarmer_ThanAtNoon()
    {
        var noon = new Atmosphere(timeOfDayHours: 12f);
        var sunset = new Atmosphere(timeOfDayHours: 18f);

        // Warmer = lower blue channel (Rayleigh-scattered light loses blue at long paths).
        sunset.SunColor.Z.Should().BeLessThan(noon.SunColor.Z);
    }

    [Fact]
    public void TimeOfDayHours_OutsideRange_IsWrapped()
    {
        // 25 hours = 1am next day. The Atmosphere should treat the input modulo 24 so the
        // UI can drive a single non-wrapping slider without flipping into a midnight cliff.
        var twentyFive = new Atmosphere(timeOfDayHours: 25f);
        var one = new Atmosphere(timeOfDayHours: 1f);

        twentyFive.SunDirection.Z.Should().BeApproximately(one.SunDirection.Z, Tolerance);
    }

    [Fact]
    public void SunGlowIntensity_NearHorizon_IsStrongerThanAtNoon()
    {
        // Forward-scatter glow ("poświata pod słońcem") swells as the sun's rays graze a longer
        // atmospheric path near the horizon. At a low afternoon sun it must clearly exceed the
        // near-nil glow of a steep midday sun.
        var lowSun = new Atmosphere(timeOfDayHours: 17f);
        var noon = new Atmosphere(timeOfDayHours: 12f);

        lowSun.SunGlowIntensity.Should().BeGreaterThan(noon.SunGlowIntensity);
    }

    [Fact]
    public void SunGlowIntensity_BelowHorizon_IsZero()
    {
        // No sun above the horizon => no forward-scatter glow at all (night is dark).
        new Atmosphere(timeOfDayHours: 0f).SunGlowIntensity.Should().Be(0f);
    }

    [Fact]
    public void SunGlowIntensity_IncreasesAsSunApproachesHorizon()
    {
        // Monotonic swell through the golden hour: each step closer to the horizon glows more.
        var early = new Atmosphere(timeOfDayHours: 16.5f);
        var mid = new Atmosphere(timeOfDayHours: 17f);
        var late = new Atmosphere(timeOfDayHours: 17.5f);

        mid.SunGlowIntensity.Should().BeGreaterThan(early.SunGlowIntensity);
        late.SunGlowIntensity.Should().BeGreaterThan(mid.SunGlowIntensity);
    }

    [Fact]
    public void SunGlowIntensity_IsInUnitRange()
    {
        new Atmosphere(timeOfDayHours: 17.8f).SunGlowIntensity.Should().BeInRange(0f, 1f);
    }

    [Fact]
    public void SunGlowWidth_NearHorizon_IsWiderThanAtNoon()
    {
        // The glow halo spreads wider across the sky as the sun sinks; at noon it is a tight disc.
        var lowSun = new Atmosphere(timeOfDayHours: 17f);
        var noon = new Atmosphere(timeOfDayHours: 12f);

        lowSun.SunGlowWidth.Should().BeGreaterThan(noon.SunGlowWidth);
    }

    [Fact]
    public void SunGlowWidth_BelowHorizon_IsZero()
    {
        new Atmosphere(timeOfDayHours: 0f).SunGlowWidth.Should().Be(0f);
    }

    [Fact]
    public void BloomIntensity_AtGoldenHour_ExceedsNoon()
    {
        // Bright-region bleed is strongest when the low, intense sun and luminous horizon sky dominate
        // the frame; a steep midday sun blooms far less.
        var goldenHour = new Atmosphere(timeOfDayHours: 17f);
        var noon = new Atmosphere(timeOfDayHours: 12f);

        goldenHour.BloomIntensity.Should().BeGreaterThan(noon.BloomIntensity);
    }

    [Fact]
    public void BloomIntensity_BelowHorizon_IsZero()
    {
        // No sun above the horizon => nothing bright enough to bleed.
        new Atmosphere(timeOfDayHours: 0f).BloomIntensity.Should().Be(0f);
    }

    [Fact]
    public void BloomIntensity_IsInUnitRange()
    {
        new Atmosphere(timeOfDayHours: 17f).BloomIntensity.Should().BeInRange(0f, 1f);
    }

    [Fact]
    public void BloomThreshold_AtGoldenHour_IsLowerThanNoon()
    {
        // A more permissive (lower) bright-pass threshold at golden hour lets the warm sky bloom; at noon
        // the threshold rises so only genuinely bright highlights (sun disc, snow) bleed.
        var goldenHour = new Atmosphere(timeOfDayHours: 17f);
        var noon = new Atmosphere(timeOfDayHours: 12f);

        goldenHour.BloomThreshold.Should().BeLessThan(noon.BloomThreshold);
    }

    [Fact]
    public void BloomThreshold_StaysInSaneRange()
    {
        // Threshold is a normalised luminance cutoff — never so low everything blooms, nor above white.
        foreach (float hour in new[] { 6f, 12f, 17f, 23f })
        {
            new Atmosphere(timeOfDayHours: hour).BloomThreshold.Should().BeInRange(0.5f, 1.0f);
        }
    }
}