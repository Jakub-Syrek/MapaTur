namespace MapaTur.Application.Terrain;

public sealed record HybridTerrainDrawPlan(
    IReadOnlyList<HybridTerrainPageKey> Pages,
    IReadOnlyList<HybridTerrainPageKey> LegacyDemFallbacks);

/// <summary>
/// Resolves an ideal quadtree cut against currently resident RMP3 pages. A resident ancestor replaces every
/// requested descendant below it as one atomic draw region, so parent and child geometry can never overlap.
/// If neither a requested page nor an ancestor is resident, the existing DEM remains the explicit fallback.
/// </summary>
public static class HybridTerrainResidencyPlanner
{
    private const byte MaximumLod = 2;

    public static HybridTerrainDrawPlan Resolve(
        IReadOnlyCollection<HybridTerrainPageKey> requested,
        IReadOnlySet<HybridTerrainPageKey> resident)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(resident);
        ValidateKeys(requested, nameof(requested));
        ValidateKeys(resident, nameof(resident));
        ValidateCut(requested);

        var candidates = new HashSet<HybridTerrainPageKey>();
        var legacyFallbacks = new HashSet<HybridTerrainPageKey>();
        foreach (HybridTerrainPageKey key in requested)
        {
            HybridTerrainPageKey candidate = key;
            while (!resident.Contains(candidate) && candidate.Lod < MaximumLod)
            {
                candidate = ParentOf(candidate);
            }

            if (resident.Contains(candidate))
            {
                candidates.Add(candidate);
            }
            else
            {
                legacyFallbacks.Add(key);
            }
        }

        HybridTerrainPageKey[] pages = candidates
            .Where(candidate => !candidates.Any(
                possibleAncestor => IsStrictAncestorOf(possibleAncestor, candidate)))
            .OrderByDescending(key => key.Lod)
            .ThenBy(key => key.PageX)
            .ThenBy(key => key.PageY)
            .ToArray();
        HybridTerrainPageKey[] fallbacks = legacyFallbacks
            .Where(fallback => !pages.Any(page => Covers(page, fallback)))
            .OrderByDescending(key => key.Lod)
            .ThenBy(key => key.PageX)
            .ThenBy(key => key.PageY)
            .ToArray();
        return new HybridTerrainDrawPlan(pages, fallbacks);
    }

    private static HybridTerrainPageKey ParentOf(HybridTerrainPageKey key) =>
        new(FloorDivideByTwo(key.PageX), FloorDivideByTwo(key.PageY), checked((byte)(key.Lod + 1)));

    private static bool Covers(HybridTerrainPageKey possibleAncestor, HybridTerrainPageKey key) =>
        possibleAncestor == key || IsStrictAncestorOf(possibleAncestor, key);

    private static bool IsStrictAncestorOf(
        HybridTerrainPageKey possibleAncestor,
        HybridTerrainPageKey possibleDescendant)
    {
        if (possibleAncestor.Lod <= possibleDescendant.Lod)
        {
            return false;
        }

        int divisor = 1 << (possibleAncestor.Lod - possibleDescendant.Lod);
        return FloorDivide(possibleDescendant.PageX, divisor) == possibleAncestor.PageX
            && FloorDivide(possibleDescendant.PageY, divisor) == possibleAncestor.PageY;
    }

    private static int FloorDivideByTwo(int value) => FloorDivide(value, 2);

    private static int FloorDivide(int value, int positiveDivisor)
    {
        int quotient = value / positiveDivisor;
        int remainder = value % positiveDivisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }

    private static void ValidateKeys(
        IEnumerable<HybridTerrainPageKey> keys,
        string parameterName)
    {
        if (keys.Any(key => key.Lod > MaximumLod))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void ValidateCut(IReadOnlyCollection<HybridTerrainPageKey> requested)
    {
        var keys = requested.ToHashSet();
        foreach (HybridTerrainPageKey key in keys)
        {
            HybridTerrainPageKey ancestor = key;
            while (ancestor.Lod < MaximumLod)
            {
                ancestor = ParentOf(ancestor);
                if (keys.Contains(ancestor))
                {
                    throw new ArgumentException(
                        "The requested RMP3 quadtree cut contains overlapping ancestor and descendant pages.",
                        nameof(requested));
                }
            }
        }
    }
}
