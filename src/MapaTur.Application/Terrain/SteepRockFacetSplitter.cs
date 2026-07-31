using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Recursively partitions oversized coherent rock faces with seeded, off-centre cuts. The pieces exactly
/// cover the source facet but do not form repeated rows or columns, keeping each scan near its natural scale.
/// </summary>
public static class SteepRockFacetSplitter
{
    public static IReadOnlyList<SteepRockRegion> Split(
        IReadOnlyList<SteepRockRegion> facets,
        float maximumWidthMeters,
        float maximumHeightMeters,
        int seed)
    {
        ArgumentNullException.ThrowIfNull(facets);
        if (!float.IsFinite(maximumWidthMeters)
            || maximumWidthMeters <= 0f
            || !float.IsFinite(maximumHeightMeters)
            || maximumHeightMeters <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumWidthMeters));
        }

        var output = new List<SteepRockRegion>();
        for (int index = 0; index < facets.Count; index++)
        {
            SplitFacet(facets[index], Hash(seed, index, facets.Count), output);
        }

        return output;

        void SplitFacet(SteepRockRegion facet, uint hash, List<SteepRockRegion> destination)
        {
            if (facet.WidthMeters <= maximumWidthMeters
                && facet.HeightMeters <= maximumHeightMeters)
            {
                destination.Add(facet);
                return;
            }

            bool splitWidth =
                (facet.WidthMeters / maximumWidthMeters) >= (facet.HeightMeters / maximumHeightMeters);
            float ratio = Lerp(0.42f, 0.58f, UnitFloat(hash));
            int firstSamples = facet.SteepSampleCount <= 1
                ? facet.SteepSampleCount
                : Math.Clamp(
                    (int)MathF.Round(facet.SteepSampleCount * ratio),
                    1,
                    facet.SteepSampleCount - 1);
            int secondSamples = facet.SteepSampleCount - firstSamples;
            if (splitWidth)
            {
                Vector3 outward = Vector3.Normalize(facet.OutwardNormal);
                Vector3 tangent = Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, outward));
                float firstWidth = facet.WidthMeters * ratio;
                float secondWidth = facet.WidthMeters - firstWidth;
                var first = facet with
                {
                    Center = facet.Center - (tangent * (secondWidth * 0.5f)),
                    WidthMeters = firstWidth,
                    SteepSampleCount = firstSamples,
                };
                var second = facet with
                {
                    Center = facet.Center + (tangent * (firstWidth * 0.5f)),
                    WidthMeters = secondWidth,
                    SteepSampleCount = secondSamples,
                };
                SplitFacet(first, Hash(hash, 0x9e3779b9u), destination);
                SplitFacet(second, Hash(hash, 0x85ebca6bu), destination);
            }
            else
            {
                float firstHeight = facet.HeightMeters * ratio;
                float secondHeight = facet.HeightMeters - firstHeight;
                var first = facet with
                {
                    Center = facet.Center - (Vector3.UnitZ * (secondHeight * 0.5f)),
                    HeightMeters = firstHeight,
                    SteepSampleCount = firstSamples,
                };
                var second = facet with
                {
                    Center = facet.Center + (Vector3.UnitZ * (firstHeight * 0.5f)),
                    HeightMeters = secondHeight,
                    SteepSampleCount = secondSamples,
                };
                SplitFacet(first, Hash(hash, 0xc2b2ae35u), destination);
                SplitFacet(second, Hash(hash, 0x27d4eb2fu), destination);
            }
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
