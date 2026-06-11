using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// The alpine zonation bands the terrain is painted with, from the valley up: subalpine
/// <see cref="Meadow"/> (hale), <see cref="Scree"/> talus (piargi), bare <see cref="Rock"/> on the
/// steep faces, then <see cref="Snow"/> and permanent <see cref="Ice"/> on the highest ground.
/// </summary>
public enum Biome
{
    /// <summary>Gentle low ground — grass / subalpine meadow (hale).</summary>
    Meadow = 0,

    /// <summary>Medium-steep or mid-elevation loose talus below the cliffs (piargi).</summary>
    Scree = 1,

    /// <summary>Steep bare rock faces at any elevation (continues the rock mask).</summary>
    Rock = 2,

    /// <summary>High, non-steep ground that holds snow.</summary>
    Snow = 3,

    /// <summary>The highest, coldest ground — permanent snow/ice.</summary>
    Ice = 4,
}

/// <summary>
/// Tunable thresholds that place the biome boundaries. A single immutable value so the classifier and
/// the terrain shader share one source of truth and presets can be A/B tested on device.
/// </summary>
/// <param name="RockSlopeDegrees">Slope at/above which a face is bare rock, regardless of elevation.</param>
/// <param name="ScreeSlopeDegrees">Slope at/above which non-rock low ground is talus (scree) not meadow.</param>
/// <param name="MeadowMaxElevationM">Elevation above which even gentle ground is scree, not meadow.</param>
/// <param name="SnowElevationM">Effective (aspect-adjusted) elevation at/above which gentle ground is snow.</param>
/// <param name="IceElevationM">Effective (aspect-adjusted) elevation at/above which gentle ground is ice.</param>
/// <param name="AspectElevationShiftM">
/// How far the snow/ice lines shift with aspect: a fully north-facing slope (northness = +1) is this many
/// metres "colder" (snowline lowered), a south-facing slope (−1) this many metres warmer.
/// </param>
public readonly record struct BiomeThresholds(
    double RockSlopeDegrees,
    double ScreeSlopeDegrees,
    double MeadowMaxElevationM,
    double SnowElevationM,
    double IceElevationM,
    double AspectElevationShiftM)
{
    /// <summary>Default Tatra-tuned thresholds (Rysy 2499 m, summer snow patches on north faces ~2300 m).</summary>
    public static BiomeThresholds Default => new(
        RockSlopeDegrees: 50.0,
        ScreeSlopeDegrees: 30.0,
        MeadowMaxElevationM: 1800.0,
        SnowElevationM: 2200.0,
        IceElevationM: 2400.0,
        AspectElevationShiftM: 150.0);
}

/// <summary>
/// Classifies a terrain sample into an alpine <see cref="Biome"/> from its elevation, slope angle and
/// aspect (northness). Pure and deterministic; the terrain fragment shader mirrors the same decision so
/// the on-screen materials match this single source of truth. Extends the steep-rock mask used for the
/// granite material.
/// </summary>
public static class BiomeClassifier
{
    /// <summary>
    /// Returns the biome for one sample.
    /// </summary>
    /// <param name="elevationM">Elevation above sea level, metres.</param>
    /// <param name="slopeDegrees">Slope angle from horizontal, degrees (0 = flat, 90 = vertical).</param>
    /// <param name="northness">
    /// Aspect as the north component of the surface normal in [−1, 1]: +1 fully north-facing (cold),
    /// −1 fully south-facing (warm), 0 flat/east-west.
    /// </param>
    /// <param name="thresholds">Boundary thresholds; defaults to <see cref="BiomeThresholds.Default"/>.</param>
    public static Biome Classify(
        double elevationM,
        double slopeDegrees,
        double northness,
        BiomeThresholds thresholds = default)
    {
        BiomeThresholds t = thresholds.Equals(default(BiomeThresholds)) ? BiomeThresholds.Default : thresholds;

        // 1) Steep faces are bare rock at any elevation — continues the granite rock mask.
        if (slopeDegrees >= t.RockSlopeDegrees)
        {
            return Biome.Rock;
        }

        // 2) Aspect shifts the cold (snow/ice) zones: north-facing ground reads "higher" (colder),
        //    south-facing "lower" (warmer).
        double effectiveElevationM = elevationM + (Math.Clamp(northness, -1.0, 1.0) * t.AspectElevationShiftM);

        if (effectiveElevationM >= t.IceElevationM)
        {
            return Biome.Ice;
        }

        if (effectiveElevationM >= t.SnowElevationM)
        {
            return Biome.Snow;
        }

        // 3) Below the snowline: medium-steep ground or anything above the meadow ceiling is talus (scree);
        //    gentle low ground is meadow (hala).
        if (slopeDegrees >= t.ScreeSlopeDegrees || elevationM >= t.MeadowMaxElevationM)
        {
            return Biome.Scree;
        }

        return Biome.Meadow;
    }
}

/// <summary>
/// The fixed material/albedo ramp per <see cref="Biome"/>: warm meadow green, grey-beige scree, granite
/// rock, bright snow, pale-blue ice. Pure so the renderer can upload it as a uniform array kept in sync
/// with <see cref="BiomeClassifier"/>.
/// </summary>
public static class BiomePalette
{
    // Index = (int)Biome. Meadow, Scree, Rock, Snow, Ice.
    private static readonly Vector3[] Colours =
    {
        new(0.388f, 0.561f, 0.337f), // Meadow — warm subalpine green
        new(0.706f, 0.671f, 0.596f), // Scree  — light grey-beige talus
        new(0.498f, 0.486f, 0.471f), // Rock   — granite grey
        new(0.965f, 0.972f, 0.984f), // Snow   — bright white
        new(0.855f, 0.910f, 0.965f), // Ice    — pale blue-white
    };

    /// <summary>Material colour (RGB 0..1) for a biome, clamped to the valid range.</summary>
    public static Vector3 ColorFor(Biome biome) => Colours[Math.Clamp((int)biome, 0, Colours.Length - 1)];

    /// <summary>The whole ramp, in <see cref="Biome"/> order — for uploading as a shader uniform array.</summary>
    public static IReadOnlyList<Vector3> All => Colours;
}