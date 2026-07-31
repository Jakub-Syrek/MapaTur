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
    int InstanceId,
    float WidthMeters);

/// <summary>
/// Builds an over-complete deterministic patch layout without rows, columns or a repeating lattice. A seeded
/// farthest-point sampler keeps adding scan centres at the least-covered wall locations; bounded jitter, scale
/// and roll then prevent the remaining sample lattice from becoming visible. Spatial neighbours use different
/// scans, and conservative interior footprints keep the requested wall covered despite irregular scan outlines.
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
        float sampleStep = MathF.Min(minimumWidth, minimumHeight) * 0.22f;
        int sampleColumns = checked((int)MathF.Ceiling(options.WidthMeters / sampleStep) + 1);
        int sampleRows = checked((int)MathF.Ceiling(options.HeightMeters / sampleStep) + 1);
        float sampleStepU = options.WidthMeters / (sampleColumns - 1);
        float sampleStepV = options.HeightMeters / (sampleRows - 1);
        var samples = new Vector2[checked(sampleColumns * sampleRows)];
        for (int row = 0; row < sampleRows; row++)
        {
            float v = (-options.HeightMeters * 0.5f) + (row * sampleStepV);
            for (int column = 0; column < sampleColumns; column++)
            {
                float u = (-options.WidthMeters * 0.5f) + (column * sampleStepU);
                samples[(row * sampleColumns) + column] = new Vector2(u, v);
            }
        }

        var covered = new bool[samples.Length];
        var footprintCenters = new List<Vector2>();
        var patches = new List<RockWallCoveragePatch>();
        while (covered.Any(value => !value))
        {
            if (patches.Count >= 512)
            {
                throw new InvalidDataException("Stochastic rock coverage did not converge.");
            }

            int instanceId = patches.Count;
            int selectedSample = SelectFarthestUncovered(
                samples,
                covered,
                footprintCenters,
                options.Seed,
                instanceId);
            uint hash = Hash(options.Seed, instanceId, selectedSample);
            int variant = (int)(hash % (uint)variantAspectRatios.Count);
            float scale = Lerp(MinimumScale, MaximumScale, UnitFloat(Hash(hash, 0x9e3779b9u)));
            float patchHeight = options.NominalPatchHeightMeters * scale;
            float patchWidth = patchHeight * variantAspectRatios[variant];
            float jitterRadius = sampleStep * 0.42f;
            float offsetU = samples[selectedSample].X
                + Lerp(-jitterRadius, jitterRadius, UnitFloat(Hash(hash, 0x85ebca6bu)));
            float offsetV = samples[selectedSample].Y
                + Lerp(-jitterRadius, jitterRadius, UnitFloat(Hash(hash, 0xc2b2ae35u)));
            float roll = Lerp(
                -MaximumRollRadians,
                MaximumRollRadians,
                UnitFloat(Hash(hash, 0x27d4eb2fu)));
            var footprintCenter = new Vector2(offsetU, offsetV);
            footprintCenters.Add(footprintCenter);
            Vector3 center = options.Center + (tangent * offsetU) + (up * offsetV);
            patches.Add(new RockWallCoveragePatch(
                new RockScanPatchPlacement(
                    center,
                    outward,
                    patchHeight,
                    options.DepthMeters * scale,
                    roll),
                variant,
                instanceId,
                patchWidth));

            float conservativeFootprint = 1f - options.OverlapFraction;
            float conservativeWidth = patchHeight * variantAspectRatios.Min();
            for (int sample = 0; sample < samples.Length; sample++)
            {
                covered[sample] |= IsInsideRotatedFootprint(
                    samples[sample],
                    footprintCenter,
                    conservativeWidth * conservativeFootprint,
                    patchHeight * conservativeFootprint,
                    roll);
            }
        }

        return AssignSpatialVariants(
            patches,
            footprintCenters,
            variantAspectRatios,
            options.Seed);
    }

    private static int SelectFarthestUncovered(
        IReadOnlyList<Vector2> samples,
        IReadOnlyList<bool> covered,
        IReadOnlyList<Vector2> centers,
        int seed,
        int instanceId)
    {
        int selected = -1;
        float selectedScore = float.NegativeInfinity;
        for (int sample = 0; sample < samples.Count; sample++)
        {
            if (covered[sample])
            {
                continue;
            }

            uint hash = Hash(seed, instanceId, sample);
            if (centers.Count == 0)
            {
                float score = UnitFloat(hash);
                if (score > selectedScore)
                {
                    selected = sample;
                    selectedScore = score;
                }

                continue;
            }

            float nearestSquared = centers.Min(center => Vector2.DistanceSquared(samples[sample], center));
            float noise = Lerp(0.86f, 1.14f, UnitFloat(hash));
            float weightedScore = nearestSquared * noise;
            if (weightedScore > selectedScore)
            {
                selected = sample;
                selectedScore = weightedScore;
            }
        }

        return selected >= 0
            ? selected
            : throw new InvalidDataException("Stochastic coverage lost its next uncovered sample.");
    }

    private static IReadOnlyList<RockWallCoveragePatch> AssignSpatialVariants(
        IReadOnlyList<RockWallCoveragePatch> patches,
        IReadOnlyList<Vector2> centers,
        IReadOnlyList<float> variantAspectRatios,
        int seed)
    {
        var neighbours = Enumerable.Range(0, patches.Count)
            .Select(_ => new HashSet<int>())
            .ToArray();
        for (int index = 0; index < patches.Count; index++)
        {
            int nearest = Enumerable.Range(0, patches.Count)
                .Where(other => other != index)
                .MinBy(other => Vector2.DistanceSquared(centers[index], centers[other]));
            neighbours[index].Add(nearest);
            neighbours[nearest].Add(index);
        }

        int[] order = Enumerable.Range(0, patches.Count)
            .OrderByDescending(index => neighbours[index].Count)
            .ThenBy(index => Hash(seed, index, patches.Count))
            .ToArray();
        var variants = Enumerable.Repeat(-1, patches.Count).ToArray();
        if (!Assign(0))
        {
            throw new InvalidDataException("Cannot assign non-repeating scan variants to spatial neighbours.");
        }

        return patches
            .Select((patch, index) => patch with
            {
                VariantIndex = variants[index],
                WidthMeters = patch.Placement.HeightMeters * variantAspectRatios[variants[index]],
            })
            .ToArray();

        bool Assign(int orderedIndex)
        {
            if (orderedIndex == order.Length)
            {
                return true;
            }

            int patchIndex = order[orderedIndex];
            uint hash = Hash(seed, patchIndex, orderedIndex);
            int firstVariant = (int)(hash % (uint)variantAspectRatios.Count);
            for (int offset = 0; offset < variantAspectRatios.Count; offset++)
            {
                int candidate = (firstVariant + offset) % variantAspectRatios.Count;
                if (neighbours[patchIndex].Any(neighbour => variants[neighbour] == candidate))
                {
                    continue;
                }

                variants[patchIndex] = candidate;
                if (Assign(orderedIndex + 1))
                {
                    return true;
                }

                variants[patchIndex] = -1;
            }

            return false;
        }
    }

    private static bool IsInsideRotatedFootprint(
        Vector2 point,
        Vector2 center,
        float width,
        float height,
        float roll)
    {
        Vector2 delta = point - center;
        float cosine = MathF.Cos(roll);
        float sine = MathF.Sin(roll);
        float localU = (delta.X * cosine) + (delta.Y * sine);
        float localV = (-delta.X * sine) + (delta.Y * cosine);
        return MathF.Abs(localU) <= width * 0.5f
            && MathF.Abs(localV) <= height * 0.5f;
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

    private static bool IsPositive(float value) => float.IsFinite(value) && value > 0f;

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
