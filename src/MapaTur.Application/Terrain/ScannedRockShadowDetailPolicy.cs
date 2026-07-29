namespace MapaTur.Application.Terrain;

/// <summary>
/// Decides whether bounded scanned-rock relief can affect a cascaded shadow map at its current texel scale.
/// When it cannot, the underlying DEM remains the conservative macro-shadow caster.
/// </summary>
public static class ScannedRockShadowDetailPolicy
{
    public static bool ShouldRender(
        float maximumReliefMeters,
        float cascadeFarMeters,
        float fieldOfViewYRadians,
        int shadowMapSize,
        float minimumReliefTexels)
    {
        if (!float.IsFinite(maximumReliefMeters)
            || maximumReliefMeters < 0f
            || !float.IsFinite(cascadeFarMeters)
            || cascadeFarMeters <= 0f
            || !float.IsFinite(fieldOfViewYRadians)
            || fieldOfViewYRadians <= 0f
            || fieldOfViewYRadians >= MathF.PI
            || shadowMapSize <= 0
            || !float.IsFinite(minimumReliefTexels)
            || minimumReliefTexels <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumReliefMeters));
        }

        float cascadeWorldHeight =
            2f * cascadeFarMeters * MathF.Tan(fieldOfViewYRadians * 0.5f);
        float worldMetersPerShadowTexel = cascadeWorldHeight / shadowMapSize;
        return maximumReliefMeters / worldMetersPerShadowTexel >= minimumReliefTexels;
    }
}
