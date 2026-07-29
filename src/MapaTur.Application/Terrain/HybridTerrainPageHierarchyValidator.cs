namespace MapaTur.Application.Terrain;

/// <summary>
/// Rejects incomplete RMP3 packages before publication. Every fine page must have a resident-capable
/// parent chain and a parent may never claim less geometric error than its child.
/// </summary>
public static class HybridTerrainPageHierarchyValidator
{
    private const byte MaximumLod = 2;
    private const float BoundsToleranceMeters = 0.001f;

    public static void Validate(IReadOnlyCollection<HybridTerrainPageDescriptor> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Count == 0)
        {
            throw new InvalidDataException("The RMP3 hierarchy is empty.");
        }

        HybridTerrainPageCatalog.EnsureUniqueKeys(pages);
        IReadOnlyDictionary<HybridTerrainPageKey, HybridTerrainPageDescriptor> byKey =
            pages.ToDictionary(page => page.Key);
        foreach (HybridTerrainPageDescriptor child in pages)
        {
            if (child.Key.Lod >= MaximumLod)
            {
                continue;
            }

            HybridTerrainPageKey parentKey = ParentOf(child.Key);
            if (!byKey.TryGetValue(parentKey, out HybridTerrainPageDescriptor parent))
            {
                throw new InvalidDataException(
                    $"RMP3 page {child.Key} has no fallback parent {parentKey}.");
            }

            if (parent.GeometricError < child.GeometricError)
            {
                throw new InvalidDataException(
                    $"RMP3 parent {parent.Key} has smaller geometric error than child {child.Key}.");
            }

            if (!ContainsXY(parent, child))
            {
                throw new InvalidDataException(
                    $"RMP3 parent {parent.Key} does not cover child {child.Key}.");
            }
        }
    }

    private static HybridTerrainPageKey ParentOf(HybridTerrainPageKey key) =>
        new(FloorDivide(key.PageX, 2), FloorDivide(key.PageY, 2), checked((byte)(key.Lod + 1)));

    private static int FloorDivide(int value, int positiveDivisor)
    {
        int quotient = value / positiveDivisor;
        int remainder = value % positiveDivisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }

    private static bool ContainsXY(
        HybridTerrainPageDescriptor parent,
        HybridTerrainPageDescriptor child) =>
        parent.WorldMin.X <= child.WorldMin.X + BoundsToleranceMeters
        && parent.WorldMin.Y <= child.WorldMin.Y + BoundsToleranceMeters
        && parent.WorldMax.X + BoundsToleranceMeters >= child.WorldMax.X
        && parent.WorldMax.Y + BoundsToleranceMeters >= child.WorldMax.Y;
}
