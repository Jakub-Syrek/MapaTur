using System;
using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>What the orthophoto colour at a spot says grows / lies there — the object CLASS the scatter places.
/// The terrain geometry (slope, elevation, treeline) then decides whether and how densely.</summary>
public enum ScatterClass
{
    /// <summary>Shadow / bare earth — nothing, or a rare small prop.</summary>
    Bare = 0,

    /// <summary>Green — meadow/forest: trees, bushes, grass.</summary>
    Vegetation = 1,

    /// <summary>Grey/beige, desaturated — scree / talus / rock: boulders and stones.</summary>
    RockScree = 2,

    /// <summary>Blue-dominant — water: a hard exclusion (no props).</summary>
    Water = 3,

    /// <summary>Bright + desaturated — snow: no props (seasonal swap handles the look).</summary>
    Snow = 4,
}

/// <summary>Thresholds that place the <see cref="OrthoScatterClassifier"/> boundaries (all on 0..1 colour). A
/// single immutable value so it can be A/B tested on device; TUNE these in the 3-D render, never top-down.</summary>
/// <param name="GreennessMin">Excess-green (g − max(r,b)) at/above which a pixel is vegetation.</param>
/// <param name="RockSatMax">Saturation below which a mid-luma pixel is rock/scree (desaturated grey/beige).</param>
/// <param name="RockLumaMin">Rock/scree luma floor (darker = shadow/bare, not rock).</param>
/// <param name="RockLumaMax">Rock/scree luma ceiling (brighter = snow).</param>
/// <param name="BluenessMin">Excess-blue (b − max(r,g)) at/above which a pixel is water.</param>
/// <param name="SnowLumaMin">Luma at/above which a desaturated pixel is snow.</param>
/// <param name="SnowSatMax">Saturation below which a bright pixel is snow (not bright vegetation).</param>
public readonly record struct ScatterThresholds(
    float GreennessMin,
    float RockSatMax,
    float RockLumaMin,
    float RockLumaMax,
    float BluenessMin,
    float SnowLumaMin,
    float SnowSatMax)
{
    /// <summary>Tatra-tuned starting thresholds (retune in-render).</summary>
    public static ScatterThresholds Default => new(
        GreennessMin: 0.06f,
        RockSatMax: 0.20f,
        RockLumaMin: 0.30f,
        RockLumaMax: 0.78f,
        BluenessMin: 0.03f,
        SnowLumaMin: 0.80f,
        SnowSatMax: 0.12f);
}

/// <summary>
/// Classifies an orthophoto pixel (RGB 0..1) into a <see cref="ScatterClass"/> for the terrain scatter: green →
/// vegetation, desaturated grey/beige → rock/scree, blue → water, bright-desaturated → snow, else bare. Pure and
/// deterministic — the discriminators (excess-green, saturation, luma, excess-blue) are the standard vegetation/
/// bare-ground splits and are robust to the GUGiK ortho's physical blue shadow (shaded grass keeps positive
/// excess-green). First matching rule wins.
/// </summary>
public static class OrthoScatterClassifier
{
    /// <summary>Classifies one orthophoto colour (RGB in 0..1). <paramref name="thresholds"/> default =
    /// <see cref="ScatterThresholds.Default"/>.</summary>
    public static ScatterClass Classify(Vector3 rgb, ScatterThresholds thresholds = default)
    {
        ScatterThresholds t = thresholds.Equals(default(ScatterThresholds)) ? ScatterThresholds.Default : thresholds;

        float r = rgb.X;
        float g = rgb.Y;
        float b = rgb.Z;
        float luma = (0.299f * r) + (0.587f * g) + (0.114f * b);
        float max = MathF.Max(r, MathF.Max(g, b));
        float min = MathF.Min(r, MathF.Min(g, b));
        float saturation = max > 1e-5f ? (max - min) / max : 0f;
        float greenness = g - MathF.Max(r, b);
        float blueness = b - MathF.Max(r, g);

        if (luma >= t.SnowLumaMin && saturation < t.SnowSatMax)
        {
            return ScatterClass.Snow;
        }

        if (blueness > t.BluenessMin)
        {
            return ScatterClass.Water;
        }

        if (greenness > t.GreennessMin)
        {
            return ScatterClass.Vegetation;
        }

        if (saturation < t.RockSatMax && luma > t.RockLumaMin && luma < t.RockLumaMax)
        {
            return ScatterClass.RockScree;
        }

        return ScatterClass.Bare;
    }
}
