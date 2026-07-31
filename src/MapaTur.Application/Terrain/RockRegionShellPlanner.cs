using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Assigns one complete, original photogrammetry mesh to each coherent steep DEM region. Randomness happens
/// between naturally separated regions, never by stamping a row of overlapping scans across one wall.
/// </summary>
public static class RockRegionShellPlanner
{
    public static IReadOnlyList<RockWallCoveragePatch> Plan(
        IReadOnlyList<SteepRockRegion> regions,
        IReadOnlyList<float> variantAspectRatios,
        float maximumDepthMeters,
        int seed)
    {
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(variantAspectRatios);
        if (regions.Count == 0
            || variantAspectRatios.Count < 3
            || variantAspectRatios.Any(aspect => !float.IsFinite(aspect) || aspect <= 0f)
            || !float.IsFinite(maximumDepthMeters)
            || maximumDepthMeters <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(regions));
        }

        int[] variants = AssignVariants(regions, variantAspectRatios.Count, seed);
        var patches = new RockWallCoveragePatch[regions.Count];
        for (int index = 0; index < regions.Count; index++)
        {
            SteepRockRegion region = regions[index];
            int variant = variants[index];
            float aspect = variantAspectRatios[variant];
            uint hash = Hash(seed, index, region.SteepSampleCount);
            float roll = Lerp(
                -RockWallCoveragePlanner.MaximumRollRadians,
                RockWallCoveragePlanner.MaximumRollRadians,
                UnitFloat(Hash(hash, 0x27d4eb2fu)));
            float cosine = MathF.Abs(MathF.Cos(roll));
            float sine = MathF.Abs(MathF.Sin(roll));
            float heightForHorizontalCoverage =
                ((cosine * region.WidthMeters) + (sine * region.HeightMeters)) / aspect;
            float heightForVerticalCoverage =
                (sine * region.WidthMeters) + (cosine * region.HeightMeters);
            float overscan = Lerp(1.06f, 1.18f, UnitFloat(Hash(hash, 0x85ebca6bu)));
            float patchHeight = MathF.Max(heightForHorizontalCoverage, heightForVerticalCoverage) * overscan;
            float patchWidth = patchHeight * aspect;
            float depth = maximumDepthMeters
                * Lerp(0.72f, 1f, UnitFloat(Hash(hash, 0xc2b2ae35u)));
            patches[index] = new RockWallCoveragePatch(
                new RockScanPatchPlacement(
                    region.Center,
                    region.OutwardNormal,
                    patchHeight,
                    depth,
                    roll),
                variant,
                index,
                patchWidth);
        }

        return patches;
    }

    private static int[] AssignVariants(
        IReadOnlyList<SteepRockRegion> regions,
        int variantCount,
        int seed)
    {
        var neighbours = Enumerable.Range(0, regions.Count)
            .Select(_ => new HashSet<int>())
            .ToArray();
        if (regions.Count > 1)
        {
            for (int index = 0; index < regions.Count; index++)
            {
                int nearest = Enumerable.Range(0, regions.Count)
                    .Where(other => other != index)
                    .MinBy(other => Vector3.DistanceSquared(regions[index].Center, regions[other].Center));
                neighbours[index].Add(nearest);
                neighbours[nearest].Add(index);
            }
        }

        int[] order = Enumerable.Range(0, regions.Count)
            .OrderByDescending(index => neighbours[index].Count)
            .ThenBy(index => Hash(seed, index, regions.Count))
            .ToArray();
        var assignments = Enumerable.Repeat(-1, regions.Count).ToArray();
        var usage = new int[variantCount];
        if (!Assign(0))
        {
            throw new InvalidDataException("Cannot distribute scanned-rock patterns across steep regions.");
        }

        return assignments;

        bool Assign(int orderedIndex)
        {
            if (orderedIndex == order.Length)
            {
                return true;
            }

            int regionIndex = order[orderedIndex];
            uint hash = Hash(seed, regionIndex, orderedIndex);
            int firstVariant = (int)(hash % (uint)variantCount);
            int[] candidates = Enumerable.Range(0, variantCount)
                .OrderBy(variant => usage[variant])
                .ThenBy(variant => (variant - firstVariant + variantCount) % variantCount)
                .ToArray();
            foreach (int candidate in candidates)
            {
                if (neighbours[regionIndex].Any(neighbour => assignments[neighbour] == candidate))
                {
                    continue;
                }

                assignments[regionIndex] = candidate;
                usage[candidate]++;
                if (Assign(orderedIndex + 1))
                {
                    return true;
                }

                usage[candidate]--;
                assignments[regionIndex] = -1;
            }

            return false;
        }
    }

    private static uint Hash(int seed, int index, int salt)
    {
        uint value = unchecked((uint)seed);
        value = Hash(value, unchecked((uint)index) * 0x9e3779b9u);
        return Hash(value, unchecked((uint)salt) * 0x85ebca6bu);
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
}
