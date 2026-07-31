using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Joins neighbouring block-level steep detections into coherent rock faces before any scan is assigned.
/// Similar normals may merge transitively; opposing or spatially separated walls remain independent.
/// </summary>
public static class SteepRockRegionMerger
{
    public static IReadOnlyList<SteepRockRegion> Merge(
        IReadOnlyList<SteepRockRegion> regions,
        float maximumGapMeters,
        float maximumNormalAngleDegrees)
    {
        ArgumentNullException.ThrowIfNull(regions);
        if (regions.Count == 0)
        {
            return [];
        }

        if (!float.IsFinite(maximumGapMeters)
            || maximumGapMeters < 0f
            || !float.IsFinite(maximumNormalAngleDegrees)
            || maximumNormalAngleDegrees <= 0f
            || maximumNormalAngleDegrees >= 90f)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumGapMeters));
        }

        float minimumNormalDot = MathF.Cos(maximumNormalAngleDegrees * MathF.PI / 180f);
        var parent = Enumerable.Range(0, regions.Count).ToArray();
        for (int first = 0; first < regions.Count; first++)
        {
            for (int second = first + 1; second < regions.Count; second++)
            {
                if (CanMerge(regions[first], regions[second], maximumGapMeters, minimumNormalDot))
                {
                    Union(first, second);
                }
            }
        }

        return Enumerable.Range(0, regions.Count)
            .GroupBy(Find)
            .OrderBy(group => group.Min())
            .Select(group => MergeComponent(group.Select(index => regions[index]).ToArray()))
            .ToArray();

        int Find(int index)
        {
            while (parent[index] != index)
            {
                parent[index] = parent[parent[index]];
                index = parent[index];
            }

            return index;
        }

        void Union(int first, int second)
        {
            int rootFirst = Find(first);
            int rootSecond = Find(second);
            if (rootFirst != rootSecond)
            {
                parent[rootSecond] = rootFirst;
            }
        }
    }

    private static bool CanMerge(
        SteepRockRegion first,
        SteepRockRegion second,
        float maximumGapMeters,
        float minimumNormalDot)
    {
        Vector3 firstNormal = Vector3.Normalize(first.OutwardNormal);
        Vector3 secondNormal = Vector3.Normalize(second.OutwardNormal);
        if (Vector3.Dot(firstNormal, secondNormal) < minimumNormalDot)
        {
            return false;
        }

        Vector3 outward = Vector3.Normalize(firstNormal + secondNormal);
        Vector3 tangent = Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, outward));
        Vector3 delta = second.Center - first.Center;
        float tangentDistance = MathF.Abs(Vector3.Dot(delta, tangent));
        float depthDistance = MathF.Abs(Vector3.Dot(delta, outward));
        float elevationDistance = MathF.Abs(delta.Z);
        float tangentReach = ((first.WidthMeters + second.WidthMeters) * 0.5f) + maximumGapMeters;
        float elevationReach = ((first.HeightMeters + second.HeightMeters) * 0.5f) + maximumGapMeters;
        float depthReach = maximumGapMeters + (MathF.Min(first.WidthMeters, second.WidthMeters) * 0.25f);
        return tangentDistance <= tangentReach
            && elevationDistance <= elevationReach
            && depthDistance <= depthReach;
    }

    private static SteepRockRegion MergeComponent(IReadOnlyList<SteepRockRegion> regions)
    {
        if (regions.Count == 1)
        {
            return regions[0];
        }

        Vector3 weightedNormal = Vector3.Zero;
        double totalWeight = 0;
        foreach (SteepRockRegion region in regions)
        {
            float weight = Math.Max(1, region.SteepSampleCount);
            weightedNormal += Vector3.Normalize(region.OutwardNormal) * weight;
            totalWeight += weight;
        }

        Vector3 outward = Vector3.Normalize(weightedNormal);
        Vector3 tangent = Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, outward));
        float minimumTangent = float.PositiveInfinity;
        float maximumTangent = float.NegativeInfinity;
        float minimumElevation = float.PositiveInfinity;
        float maximumElevation = float.NegativeInfinity;
        double weightedDepth = 0;
        int totalSamples = 0;
        foreach (SteepRockRegion region in regions)
        {
            float centerTangent = Vector3.Dot(region.Center, tangent);
            minimumTangent = MathF.Min(minimumTangent, centerTangent - (region.WidthMeters * 0.5f));
            maximumTangent = MathF.Max(maximumTangent, centerTangent + (region.WidthMeters * 0.5f));
            minimumElevation = MathF.Min(minimumElevation, region.Center.Z - (region.HeightMeters * 0.5f));
            maximumElevation = MathF.Max(maximumElevation, region.Center.Z + (region.HeightMeters * 0.5f));
            float weight = Math.Max(1, region.SteepSampleCount);
            weightedDepth += Vector3.Dot(region.Center, outward) * weight;
            totalSamples = checked(totalSamples + region.SteepSampleCount);
        }

        float centerTangentMerged = (minimumTangent + maximumTangent) * 0.5f;
        float centerElevation = (minimumElevation + maximumElevation) * 0.5f;
        float centerDepth = (float)(weightedDepth / totalWeight);
        Vector3 center = (tangent * centerTangentMerged)
            + (outward * centerDepth)
            + (Vector3.UnitZ * centerElevation);
        return new SteepRockRegion(
            center,
            outward,
            maximumTangent - minimumTangent,
            maximumElevation - minimumElevation,
            totalSamples);
    }
}
