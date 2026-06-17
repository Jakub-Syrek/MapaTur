using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Time-of-day driven atmospheric model: derives sun direction, sun colour, sky gradient
/// (zenith + horizon), an ambient-light factor for the terrain Lambert pass, and a fog
/// density + colour for aerial perspective from a single <see cref="TimeOfDayHours"/>
/// input in [0,24). All outputs are deterministic and immutable so the renderer can
/// upload the resulting uniforms once per frame without locking.
///
/// Geometry assumes a fixed Tatra latitude (~49° N) and approximates the summer-solstice
/// sun arc — the visible elevation peaks at ~64° at noon, declines symmetrically to the
/// horizon at 06:00 and 18:00, and dips below at night. Azimuth sweeps east → south →
/// west across the day; world frame is +X east, +Y north, +Z up (matching the renderer).
///
/// Colour transitions are piecewise-interpolated between five canonical presets
/// (midnight, sunrise, golden, noon, sunset) keyed on the sun's elevation, so a slider
/// drive produces a smooth visual progression without explicit easing curves.
/// </summary>
public sealed class Atmosphere
{
    // Tatra-summer-solstice constants. Peak elevation chosen so the noon ray is steep but
    // not vertical — keeps the Lambert-shaded slopes interesting at the top of the day.
    private const float PeakElevationRadians = 1.118f; // ≈ 64°
    private const float SunriseHour = 6f;
    private const float SunsetHour = 18f;

    /// <summary>Input: time of day in hours, wrapped into [0,24).</summary>
    public float TimeOfDayHours { get; }

    /// <summary>Cloud coverage in [0,1]. 0 = clear sky, ~0.4 = scattered cirrus, 1.0 = overcast.</summary>
    public float CloudCoverage { get; }

    /// <summary>Wind strength in [0,1]. Drives how fast the clouds drift and how dark they get —
    /// 0 = calm + bright, 1 = gale + dark storm clouds.</summary>
    public float Wind { get; }

    /// <summary>Snow cover amount in [0,1]. 0 = no snow; 1 = full snow (the snowline drops to the
    /// valley floor). Drives the terrain shader's snow blend — whitening above a snowline that lowers
    /// as this rises, concentrated on flatter slopes so cliffs stay bare.</summary>
    public float SnowAmount { get; }

    /// <summary>Unit vector from the surface toward the sun, in world frame (X east, Y north, Z up).</summary>
    public Vector3 SunDirection { get; }

    /// <summary>RGB colour of the direct sunlight reaching the surface (Mie/Rayleigh tinted at low elevations).</summary>
    public Vector3 SunColor { get; }

    /// <summary>RGB colour of the sky directly overhead.</summary>
    public Vector3 SkyZenithColor { get; }

    /// <summary>RGB colour of the sky at the horizon ring.</summary>
    public Vector3 SkyHorizonColor { get; }

    /// <summary>Ambient-light factor in [0,1]. Combined with Lambert in the terrain shader: shade = ambient + (1 - ambient) * lambert.</summary>
    public float AmbientFactor { get; }

    /// <summary>RGB tint that distant terrain blends toward through aerial perspective.</summary>
    public Vector3 FogColor { get; }

    /// <summary>Exponential fog density per metre of view depth. Higher = murkier distance.</summary>
    public float FogDensity { get; }

    /// <summary>Strength [0,1] of the forward-scatter glow ("poświata") around and under the sun. Swells as
    /// the sun nears the horizon (longer atmospheric path scatters more forward light); zero when the sun
    /// is below the horizon. Drives the sky shader's sun-glow term.</summary>
    public float SunGlowIntensity { get; }

    /// <summary>Angular spread [0,1] of the sun-glow halo. A tight disc near noon, widening across the sky
    /// as the sun sinks; zero at night.</summary>
    public float SunGlowWidth { get; }

    /// <summary>Strength [0,1] of the screen-space bloom (bright-region bleed). Peaks at golden hour, stays
    /// gentle in daylight, and is zero at night. Scales the additive bloom composite in the post-process
    /// stage.</summary>
    public float BloomIntensity { get; }

    /// <summary>Normalised luminance cutoff [0.5,1] for the bloom bright-pass — pixels brighter than this
    /// bleed. Lower (more permissive) at golden hour so the warm sky glows; higher at noon so only the
    /// brightest highlights (sun disc, snow) bloom.</summary>
    public float BloomThreshold { get; }

    /// <summary>
    /// Builds the model from a time-of-day in hours (wrapped into [0,24)) and an optional cloud
    /// coverage in [0,1]. Default coverage = 0.35 (scattered cirrus, the look that fits a
    /// blue-sky day with high-altitude wisps).
    /// </summary>
    public Atmosphere(float timeOfDayHours, float cloudCoverage = 0.35f, float wind = 0.3f, float snow = 0f)
    {
        TimeOfDayHours = WrapToDay(timeOfDayHours);
        CloudCoverage = Math.Clamp(cloudCoverage, 0f, 1f);
        Wind = Math.Clamp(wind, 0f, 1f);
        SnowAmount = Math.Clamp(snow, 0f, 1f);

        // Sun arc: sin(π · t/12) shapes a half-cycle that peaks at noon and bottoms at midnight.
        // Scaling by PeakElevationRadians gives a max of ~64° above the horizon during the day
        // and a symmetric ~64° below at midnight, matching the visual look-and-feel users expect
        // ("the sun's gone, it's dark") without modelling civil/astronomical twilight bands.
        float dayPhase = MathF.PI * (TimeOfDayHours - SunriseHour) / (SunsetHour - SunriseHour);
        float sunElevation = PeakElevationRadians * MathF.Sin(dayPhase);

        // Azimuth: sweeps from east (90°) through south (180°) to west (270°) over the day.
        // Outside the day band the value continues monotonically so SunDirection stays
        // deterministic — the rendered sun simply sits below the horizon.
        float sunAzimuth = (MathF.PI / 2f) + (MathF.PI * (TimeOfDayHours - SunriseHour) / (SunsetHour - SunriseHour));

        float cosEl = MathF.Cos(sunElevation);
        SunDirection = Vector3.Normalize(new Vector3(
            cosEl * MathF.Sin(sunAzimuth),
            cosEl * MathF.Cos(sunAzimuth),
            MathF.Sin(sunElevation)));

        // Visual presets are keyed on a 0..1 "altitude score" — saturating sin(elevation) so
        // the horizon-to-zenith transition feels physical without modelling true scattering.
        float dayness = MathF.Max(0f, MathF.Sin(sunElevation));         // 0 at horizon / night, ~0.9 at noon
        float warmth = MathF.Max(0f, 1f - MathF.Abs(sunElevation / 0.35f));// peaks when sun is within ±20° of horizon

        // Sky colours: cool blue at zenith during the day, warm orange at the horizon during
        // sunrise/sunset, deep blue (not black) across the dome at night. The daytime zenith
        // floor is kept fairly bright so clear-sky gaps between clouds don't read as dark
        // holes at low sun angles — at golden hour the sky between cirrus streaks is still a
        // luminous blue, not navy.
        var dayZenith = new Vector3(0.33f, 0.53f, 0.86f);
        var nightZenith = new Vector3(0.03f, 0.04f, 0.10f);
        // Blend in a touch more brightness at low (but still positive) sun than dayness alone
        // would give, so sunset zenith doesn't crash to near-night while the horizon is ablaze.
        float zenithLift = MathF.Max(dayness, warmth * 0.45f);
        SkyZenithColor = Vector3.Lerp(nightZenith, dayZenith, zenithLift);

        var coolHorizon = new Vector3(0.78f, 0.84f, 0.92f);
        var warmHorizon = new Vector3(0.95f, 0.55f, 0.20f);
        var nightHorizon = new Vector3(0.05f, 0.05f, 0.10f);
        var dayHorizon = Vector3.Lerp(coolHorizon, warmHorizon, warmth);
        SkyHorizonColor = Vector3.Lerp(nightHorizon, dayHorizon, MathF.Min(1f, dayness + warmth * 0.5f));

        // Direct sun colour: white when high, deep orange when low, off when below horizon.
        var sunHigh = new Vector3(1.00f, 0.97f, 0.92f);
        var sunLow = new Vector3(1.00f, 0.50f, 0.15f);
        Vector3 sunDay = Vector3.Lerp(sunHigh, sunLow, warmth);
        SunColor = sunElevation > 0f ? sunDay : Vector3.Zero;

        // Ambient: tracks sin(elevation) directly (not the day-clamped variant) so the night
        // half of the cycle keeps falling instead of plateauing at the horizon-equivalent. At
        // noon ~0.41 to keep Lambert visible without crushing shadows; at sunset ~0.05; at
        // midnight clamped to ~0.02 so the terrain reads as dark silhouettes against the sky.
        float elevationSin = MathF.Sin(sunElevation);
        AmbientFactor = Math.Clamp(0.05f + (0.40f * elevationSin), 0.02f, 0.45f);

        // Fog: density spikes near the horizon (longer atmospheric path = more aerial
        // perspective), drops to a minimum at noon. Calibrated against the Tatra scene scale
        // (camera typically 10-50 km from the focal target, ridges up to ~80 km away):
        //   noon @ 30 km    => 1-exp(-30000*2e-6) ≈ 6% blend toward horizon — subtle haze
        //   sunset @ 30 km  => 1-exp(-30000*1.5e-5) ≈ 36% blend — visible golden veil
        //   sunset @ 80 km  => 1-exp(-80000*1.5e-5) ≈ 70% blend — far ridges fade into sky
        // The earlier numbers (45e-6 .. 180e-6) washed the entire mesh out even at noon.
        const float fogMin = 0.000002f;
        const float fogMax = 0.000015f;
        FogDensity = fogMin + ((fogMax - fogMin) * warmth);
        FogColor = SkyHorizonColor;

        // Forward-scatter glow ("poświata pod słońcem"): the warm bloom that swells around and under the
        // sun as its rays graze a longer atmospheric path near the horizon. Active only while the sun is
        // above the horizon; fades in over the last ~34° of descent. Width grows alongside it but keeps a
        // small floor so a tight halo is always present while the sun is up.
        const float glowMaxElevationRadians = 0.6f; // ≈ 34°
        float horizonProximity = sunElevation > 0f
            ? Math.Clamp(1f - (sunElevation / glowMaxElevationRadians), 0f, 1f)
            : 0f;
        SunGlowIntensity = horizonProximity;
        SunGlowWidth = sunElevation > 0f ? 0.15f + (0.85f * horizonProximity) : 0f;

        // Bloom (screen-space bright-region bleed): strongest at golden hour (low intense sun + luminous
        // horizon sky), gentle but present in daylight (bright snow / clouds still glow), and off at night.
        // The bright-pass threshold drops near the horizon so the warm sky blooms, and rises toward noon so
        // only genuinely bright highlights bleed.
        float bloomDay = MathF.Max(0f, MathF.Sin(sunElevation));
        BloomIntensity = sunElevation > 0f
            ? Math.Clamp((0.15f * bloomDay) + (0.85f * horizonProximity), 0f, 1f)
            : 0f;
        BloomThreshold = 0.65f + (0.20f * bloomDay);
    }

    private static float WrapToDay(float hours)
    {
        float wrapped = hours % 24f;
        return wrapped < 0f ? wrapped + 24f : wrapped;
    }
}