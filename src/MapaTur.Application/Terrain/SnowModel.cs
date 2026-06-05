namespace MapaTur.Application.Terrain;

/// <summary>
/// Parameters the terrain shader uses to paint snow, as a single immutable value:
/// the elevation snowline + softening band (vertical placement) and the slope-angle cosines that hold
/// snow on gentle slopes while baring rock on steep faces.
/// </summary>
/// <param name="LineZ">World-Z of the snowline; surfaces above it whiten (over <paramref name="BandZ"/>).</param>
/// <param name="BandZ">Vertical soft-edge band over which the snowline fades in.</param>
/// <param name="SlopeCosBare">cos of the slope angle at/above which the face is fully bare rock.</param>
/// <param name="SlopeCosFull">cos of the slope angle at/below which snow fully holds.</param>
public readonly record struct SnowShadingParameters(
    float LineZ,
    float BandZ,
    float SlopeCosBare,
    float SlopeCosFull);

/// <summary>
/// Computes the snow-cover shading parameters from the snow amount and the terrain's vertical extent.
/// Pure and deterministic so it can be unit-tested and reused; the terrain fragment shader mirrors the
/// same formula on the GPU from the uniforms derived here.
/// </summary>
public static class SnowModel
{
    /// <summary>Slope angle (degrees) at/below which snow fully holds (gentle slopes stay white).</summary>
    public const float SnowHoldsBelowDegrees = 38f;

    /// <summary>Slope angle (degrees) at/above which the face is fully bare rock (snow sheds off).</summary>
    public const float SnowBaresAboveDegrees = 55f;

    /// <summary>Soft snowline band as a fraction of the terrain's relief.</summary>
    private const float BandFractionOfRelief = 0.15f;

    /// <summary>
    /// Builds the snow shading parameters.
    /// </summary>
    /// <param name="snowAmount">Snow slider, clamped to 0..1 (0 = a dusting on the peaks, 1 = full cover).</param>
    /// <param name="terrainMinZ">Lowest terrain elevation (valley floor), world-Z.</param>
    /// <param name="terrainMaxZ">Highest terrain elevation (summit), world-Z.</param>
    public static SnowShadingParameters Compute(float snowAmount, float terrainMinZ, float terrainMaxZ)
    {
        float amount = Math.Clamp(snowAmount, 0f, 1f);

        // Guard against degenerate / inverted relief so the band never collapses to zero.
        float relief = MathF.Max(1f, terrainMaxZ - terrainMinZ);
        float bandZ = relief * BandFractionOfRelief;

        // Snowline = highest peak at amount≈0 (snow appears on top first), dropping to one band below the
        // valley floor at full cover (everything, floor included, whitens). Linear in the slider.
        float lineZ = (terrainMaxZ * (1f - amount)) + ((terrainMinZ - bandZ) * amount);

        float cosBare = MathF.Cos(SnowBaresAboveDegrees * (MathF.PI / 180f));
        float cosFull = MathF.Cos(SnowHoldsBelowDegrees * (MathF.PI / 180f));

        return new SnowShadingParameters(lineZ, bandZ, cosBare, cosFull);
    }
}