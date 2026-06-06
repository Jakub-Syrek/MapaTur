namespace MapaTur.Application.Terrain;

/// <summary>
/// Midday-brightness model. As the sun climbs toward its noon peak the direct light grows more
/// intense, so high-albedo snow should read as a brighter white instead of the flat grey it shows
/// at lower sun ("o 12 jest szarawo"). This turns the sun direction's vertical component
/// (z, 1 = straight overhead) into a 0..1 "noon factor" and the extra white-lift to apply to snow
/// at that elevation. Pure + deterministic so the renderer can evaluate it per frame and upload a
/// single uniform.
///
/// The app's sun arc peaks at ~64° (z ≈ 0.90) at noon — see <see cref="Atmosphere"/> — never a
/// vertical sun, so the ramp is calibrated against that real peak: it starts just below midday and
/// is full at the peak, concentrating the boost around noon while leaving the golden hours (where
/// snow legitimately takes warm/cool low-sun tints) untouched.
/// </summary>
public static class NoonLightModel
{
    /// <summary>Sun vertical component (z) at which the midday boost begins to ramp in.</summary>
    public const float RampStart = 0.78f;

    /// <summary>Sun vertical component (z) at/above which the midday boost is full — ≈ the app's noon peak.</summary>
    public const float RampFull = 0.90f;

    /// <summary>Extra fraction lifting snow toward pure white at the noon peak, on top of its base brightness.</summary>
    public const float MaxSnowWhiteLift = 0.30f;

    /// <summary>0 when the sun is low, ramping smoothly (smoothstep) to 1 as it nears its noon peak.</summary>
    public static float NoonFactor(float sunUp) => SmoothStep(RampStart, RampFull, sunUp);

    /// <summary>Extra white-lift to add on top of the base snow brightness at this sun elevation.</summary>
    public static float SnowWhiteLift(float sunUp) => NoonFactor(sunUp) * MaxSnowWhiteLift;

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}