namespace MapaTur.Application.Terrain;

/// <summary>
/// A tuning preset for the Model 1 roughness boost (fed to <see cref="ScreenSpaceLod.RoughnessFactor"/> /
/// <see cref="ScreenSpaceLod.AssignAroundLookAt"/>). Three conservative-to-bold steps to flip between on the
/// device; the hard vertex budget still caps total detail whichever is chosen.
/// </summary>
/// <param name="Name">Display name.</param>
/// <param name="ReferenceRoughnessMeters">Roughness that adds a full +1 boost.</param>
/// <param name="MaxBoost">Upper clamp on the additive boost (so the factor maxes at 1 + MaxBoost).</param>
public sealed record RoughnessLodPreset(string Name, double ReferenceRoughnessMeters, double MaxBoost)
{
    /// <summary>The largest <see cref="ScreenSpaceLod.RoughnessFactor"/> this preset can produce.</summary>
    public double MaxFactor => 1.0 + MaxBoost;

    /// <summary>Gentlest boost: roughness 40 m for +1, factor ≤ 3×.</summary>
    public static RoughnessLodPreset Safe => new("Safe", 40.0, 2.0);

    /// <summary>Default: roughness 30 m for +1, factor ≤ 4×.</summary>
    public static RoughnessLodPreset Balanced => new("Balanced", 30.0, 3.0);

    /// <summary>Boldest: roughness 20 m for +1, factor ≤ 5×.</summary>
    public static RoughnessLodPreset Aggressive => new("Aggressive", 20.0, 4.0);
}