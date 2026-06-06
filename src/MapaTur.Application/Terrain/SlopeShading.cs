using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Classifies a terrain slope angle (degrees from horizontal) into the eight avalanche-style legend
/// bands: a wide 0-20° gentle step, then 10° steps up to 80-90°. Pure and deterministic; the terrain
/// shader mirrors the same banding so the on-screen colours match this single source of truth.
/// </summary>
public static class SlopeClassification
{
    /// <summary>Number of slope bands (0-20, 20-30, … 80-90°).</summary>
    public const int BandCount = 8;

    /// <summary>
    /// Returns the band index (0..7) for the given slope angle, clamping out-of-range angles. The first
    /// band spans 0-20°; each subsequent band spans 10°. Boundaries belong to the steeper band.
    /// </summary>
    public static int Classify(double slopeDegrees)
    {
        double slope = Math.Clamp(slopeDegrees, 0.0, 90.0);
        if (slope < 20.0)
        {
            return 0;
        }

        int band = 1 + (int)Math.Floor((slope - 20.0) / 10.0);
        return Math.Min(band, BandCount - 1);
    }
}

/// <summary>
/// The fixed colour ramp for the slope-steepness map: green on gentle ground, through cream and salmon,
/// to red/violet on the extreme faces — the conventional avalanche-terrain palette. Pure so the renderer
/// can upload it as a uniform array and the bands stay consistent with <see cref="SlopeClassification"/>.
/// </summary>
public static class SlopePalette
{
    // Index = slope band (0 = 0-20° … 7 = 80-90°). Green → cream → salmon → pink → maroon → violet.
    private static readonly Vector3[] Bands =
    {
        new(0.435f, 0.682f, 0.478f), // 0-20°  green
        new(0.525f, 0.753f, 0.494f), // 20-30° green
        new(0.671f, 0.816f, 0.514f), // 30-40° light green
        new(0.925f, 0.894f, 0.800f), // 40-50° cream
        new(0.910f, 0.718f, 0.604f), // 50-60° salmon
        new(0.851f, 0.549f, 0.573f), // 60-70° pink
        new(0.690f, 0.333f, 0.400f), // 70-80° maroon-red
        new(0.431f, 0.369f, 0.557f), // 80-90° violet
    };

    /// <summary>Colour (RGB 0..1) for a slope band index, clamped to the valid range.</summary>
    public static Vector3 ColorFor(int band) => Bands[Math.Clamp(band, 0, SlopeClassification.BandCount - 1)];

    /// <summary>The whole ramp, in band order — for uploading as a shader uniform array.</summary>
    public static IReadOnlyList<Vector3> All => Bands;
}