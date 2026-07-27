using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Offline coverage controls for replacing stretched vertical ortho with a shell made from real scanned
/// geometry. The macro surface remains the DEM; patches only contribute bounded outward relief.
/// </summary>
public readonly record struct RockWallCoverageOptions(
    Vector3 Center,
    Vector3 OutwardNormal,
    float WidthMeters,
    float HeightMeters,
    float NominalPatchHeightMeters,
    float DepthMeters,
    float OverlapFraction,
    int Seed);

/// <summary>One deterministic scan instance in a wall coverage layout.</summary>
public readonly record struct RockWallCoveragePatch(
    RockScanPatchPlacement Placement,
    int VariantIndex,
    int Column,
    int Row,
    float WidthMeters);

/// <summary>
/// Builds an over-complete, deterministic patch layout. Neighbouring cells always use different scans, while
/// bounded jitter, scale and roll break the mould-like grid without opening holes at the requested boundary.
/// </summary>
public static class RockWallCoveragePlanner
{
    public const float MinimumScale = 0.88f;
    public const float MaximumScale = 1.12f;
    public const float MaximumRollRadians = MathF.PI / 24f; // 7.5 degrees

    public static IReadOnlyList<RockWallCoveragePatch> Plan(
        RockWallCoverageOptions options,
        IReadOnlyList<float> variantAspectRatios)
    {
        ArgumentNullException.ThrowIfNull(variantAspectRatios);
        Validate(options, variantAspectRatios);

        Vector3 outward = Vector3.Normalize(options.OutwardNormal);
        Vector3 up = Vector3.Normalize(Vector3.UnitZ - (outward * Vector3.Dot(Vector3.UnitZ, outward)));
        Vector3 tangent = Vector3.Normalize(Vector3.Cross(up, outward));
        float minimumHeight = options.NominalPatchHeightMeters * MinimumScale;
        float minimumWidth = minimumHeight * variantAspectRatios.Min();
        float rowStep = minimumHeight * (1f - options.OverlapFraction);
        float columnStep = minimumWidth * (1f - options.OverlapFraction);
        float jitterU = minimumWidth * options.OverlapFraction * 0.18f;
        float jitterV = minimumHeight * options.OverlapFraction * 0.18f;
        float startU = (-options.WidthMeters * 0.5f) - jitterU;
        float endU = (options.WidthMeters * 0.5f) + jitterU;
        float startV = (-options.HeightMeters * 0.5f) - jitterV;
        float endV = (options.HeightMeters * 0.5f) + jitterV;
        int columns = checked((int)MathF.Ceiling((endU - startU) / columnStep) + 1);
        int rows = checked((int)MathF.Ceiling((endV - startV) / rowStep) + 1);
        var patches = new List<RockWallCoveragePatch>(checked(columns * rows));

        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                uint hash = Hash(options.Seed, column, row);
                int variant = PositiveModulo(
                    column + row + (int)(hash % (uint)variantAspectRatios.Count),
                    variantAspectRatios.Count);
                if (column > 0 && patches[^1].VariantIndex == variant)
                {
                    variant = (variant + 1) % variantAspectRatios.Count;
                }

                if (row > 0 && patches[((row - 1) * columns) + column].VariantIndex == variant)
                {
                    variant = (variant + 1) % variantAspectRatios.Count;
                    if (column > 0 && patches[^1].VariantIndex == variant)
                    {
                        variant = (variant + 1) % variantAspectRatios.Count;
                    }
                }

                float scale = Lerp(MinimumScale, MaximumScale, UnitFloat(Hash(hash, 0x9e3779b9u)));
                float patchHeight = options.NominalPatchHeightMeters * scale;
                float patchWidth = patchHeight * variantAspectRatios[variant];
                float offsetU = startU + (column * columnStep)
                    + Lerp(-jitterU, jitterU, UnitFloat(Hash(hash, 0x85ebca6bu)));
                float offsetV = startV + (row * rowStep)
                    + Lerp(-jitterV, jitterV, UnitFloat(Hash(hash, 0xc2b2ae35u)));
                float roll = Lerp(
                    -MaximumRollRadians,
                    MaximumRollRadians,
                    UnitFloat(Hash(hash, 0x27d4eb2fu)));
                Vector3 center = options.Center + (tangent * offsetU) + (up * offsetV);
                patches.Add(new RockWallCoveragePatch(
                    new RockScanPatchPlacement(
                        center,
                        outward,
                        patchHeight,
                        options.DepthMeters * scale,
                        roll),
                    variant,
                    column,
                    row,
                    patchWidth));
            }
        }

        return patches;
    }

    private static void Validate(
        RockWallCoverageOptions options,
        IReadOnlyList<float> variantAspectRatios)
    {
        if (!IsFinite(options.Center)
            || !IsFinite(options.OutwardNormal)
            || options.OutwardNormal.LengthSquared() < 0.25f)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Coverage frame must be finite.");
        }

        Vector3 outward = Vector3.Normalize(options.OutwardNormal);
        if ((Vector3.UnitZ - (outward * Vector3.Dot(Vector3.UnitZ, outward))).LengthSquared() < 0.01f)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Coverage normal cannot be vertical.");
        }

        if (!IsPositive(options.WidthMeters)
            || !IsPositive(options.HeightMeters)
            || !IsPositive(options.NominalPatchHeightMeters)
            || !IsPositive(options.DepthMeters)
            || !float.IsFinite(options.OverlapFraction)
            || options.OverlapFraction < 0.15f
            || options.OverlapFraction > 0.45f)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Coverage dimensions or overlap are invalid.");
        }

        if (variantAspectRatios.Count < 3
            || variantAspectRatios.Any(aspect => !IsPositive(aspect)))
        {
            throw new ArgumentException(
                "Coverage needs at least three finite, positive scan variants.",
                nameof(variantAspectRatios));
        }
    }

    private static uint Hash(int seed, int column, int row)
    {
        uint value = unchecked((uint)seed);
        value = Hash(value, unchecked((uint)column) * 0x9e3779b9u);
        return Hash(value, unchecked((uint)row) * 0x85ebca6bu);
    }

    private static uint Hash(uint value, uint salt)
    {
        value ^= salt + 0x9e3779b9u + (value << 6) + (value >> 2);
        value ^= value >> 16;
        value *= 0x7feb352du;
        value ^= value >> 15;
        value *= 0x846ca68bu;
        return value ^ (value >> 16);
    }

    private static float UnitFloat(uint value) => (value & 0x00ffffffu) / 16777215f;

    private static float Lerp(float start, float end, float amount) => start + ((end - start) * amount);

    private static int PositiveModulo(int value, int divisor) => ((value % divisor) + divisor) % divisor;

    private static bool IsPositive(float value) => float.IsFinite(value) && value > 0f;

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
