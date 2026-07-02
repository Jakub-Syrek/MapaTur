namespace MapaTur.Application.Terrain;

/// <summary>The residency diff for one selection update: baked tiles to load and tiles to evict.</summary>
/// <param name="ToLoad">Desired tiles not yet resident, in the desired (near→far) order, capped to the
/// per-call max-concurrent-loads budget so a big jump streams in over several updates instead of stalling.</param>
/// <param name="ToEvict">Resident tiles no longer desired, farthest-first (the reverse of the resident
/// near→far order), so the far field is released before anything close to the camera.</param>
public sealed record TileResidencyDiff(IReadOnlyList<DemTileKey> ToLoad, IReadOnlyList<DemTileKey> ToEvict);

/// <summary>
/// Stage 2b of the terrain re-architecture: the residency diff policy that turns a quadtree selection change
/// into streaming work. Given what is currently resident and what the selector now wants (both ordered
/// near→far) plus a max-concurrent-loads budget, it returns a deterministic <see cref="TileResidencyDiff"/>:
/// the desired-but-not-resident tiles to load (near→far, capped per call) and the resident-but-not-desired
/// tiles to evict (farthest-first).
///
/// Pure function — no state, no disk, no GL. The caller drives streaming with the result and calls again as
/// the selection moves; the per-call load cap means a large change is paged in over successive updates while
/// the diff stays small and predictable. Distance ordering is taken from the supplied list order (the selector
/// already sorts near→far), so eviction "farthest-first" is simply the reverse of the resident order — no
/// distance recomputation, fully deterministic.
/// </summary>
public static class TileResidencyPlanner
{
    /// <summary>
    /// Computes the load/evict diff between <paramref name="resident"/> and <paramref name="desired"/>.
    /// </summary>
    /// <param name="resident">Tiles currently resident, ordered near→far (the order eviction reverses to release
    /// the far field first). Order within is otherwise preserved; duplicates are ignored.</param>
    /// <param name="desired">Tiles the selector now wants, ordered near→far. <see cref="TileResidencyDiff.ToLoad"/>
    /// keeps this order so the nearest missing tiles stream first. Duplicates are ignored.</param>
    /// <param name="maxConcurrentLoads">Most tiles to schedule for loading this call (≥ 1). Excess desired-but-
    /// missing tiles are left for a later call (they reappear in <paramref name="desired"/> until resident).</param>
    /// <returns>The deterministic load/evict diff. Empty <see cref="TileResidencyDiff.ToLoad"/> and
    /// <see cref="TileResidencyDiff.ToEvict"/> when <paramref name="resident"/> already equals
    /// <paramref name="desired"/> as a set.</returns>
    /// <exception cref="ArgumentNullException">A list argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxConcurrentLoads"/> is below 1.</exception>
    public static TileResidencyDiff Plan(
        IReadOnlyList<DemTileKey> resident,
        IReadOnlyList<DemTileKey> desired,
        int maxConcurrentLoads)
    {
        ArgumentNullException.ThrowIfNull(resident);
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrentLoads, 1);

        var residentSet = new HashSet<DemTileKey>(resident);
        var desiredSet = new HashSet<DemTileKey>(desired);

        // ToLoad: desired tiles not resident, in the desired (near→far) order, capped to the per-call budget.
        // A HashSet guards against a duplicated desired key scheduling the same load twice.
        var toLoad = new List<DemTileKey>(Math.Min(maxConcurrentLoads, desired.Count));
        var loadSeen = new HashSet<DemTileKey>();
        foreach (DemTileKey key in desired)
        {
            if (toLoad.Count >= maxConcurrentLoads)
            {
                break;
            }

            if (!residentSet.Contains(key) && loadSeen.Add(key))
            {
                toLoad.Add(key);
            }
        }

        // ToEvict: resident tiles no longer desired, farthest-first = reverse of the resident near→far order.
        var toEvict = new List<DemTileKey>();
        var evictSeen = new HashSet<DemTileKey>();
        for (int i = resident.Count - 1; i >= 0; i--)
        {
            DemTileKey key = resident[i];
            if (!desiredSet.Contains(key) && evictSeen.Add(key))
            {
                toEvict.Add(key);
            }
        }

        return new TileResidencyDiff(toLoad, toEvict);
    }
}