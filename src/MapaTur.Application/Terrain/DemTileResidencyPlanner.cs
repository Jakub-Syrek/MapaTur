namespace MapaTur.Application.Terrain;

/// <summary>The work an update's residency decision implies: DEM tiles to load and tiles to evict.</summary>
/// <param name="ToLoad">Needed tiles that are not yet resident.</param>
/// <param name="ToEvict">Resident tiles dropped to stay within the budget (never a tile needed this update).</param>
public sealed record DemTileResidencyPlan(IReadOnlyList<DemTileKey> ToLoad, IReadOnlyList<DemTileKey> ToEvict);

/// <summary>
/// Decides which DEM tiles to keep resident while streaming wider 1 m coverage. Each update it is told which
/// tiles the viewport needs; it loads the newly-needed ones, keeps recently-used off-screen tiles until the
/// budget is pressured (hysteresis → no reload thrash when panning back), and evicts the least-recently-used
/// off-screen tiles first. An update whose needed set alone exceeds the budget keeps them all (you can't render
/// a hole) — the budget is enforced only against tiles that have fallen out of view. Pure, keyed by
/// <see cref="DemTileKey"/>; mirrors <see cref="OrthoResidencyPlanner"/>. No I/O, no rendering.
/// </summary>
public sealed class DemTileResidencyPlanner
{
    private readonly int maxResidentTiles;

    // Most-recently-used order: index 0 is the least-recently-used tile, last is the most recent.
    private readonly List<DemTileKey> recency = new();
    private readonly HashSet<DemTileKey> resident = new();

    /// <summary>Creates a planner that targets at most <paramref name="maxResidentTiles"/> resident tiles.</summary>
    public DemTileResidencyPlanner(int maxResidentTiles)
    {
        if (maxResidentTiles <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxResidentTiles), "Budget must be positive.");
        }

        this.maxResidentTiles = maxResidentTiles;
    }

    /// <summary>The tiles currently resident.</summary>
    public IReadOnlyCollection<DemTileKey> Resident => resident;

    /// <summary>
    /// Records the tiles needed this update and returns the load/evict work. Needed tiles are marked
    /// most-recently-used; new ones are scheduled to load; tiles beyond the budget that are no longer needed
    /// are evicted least-recently-used first. Needed tiles are never evicted.
    /// </summary>
    public DemTileResidencyPlan Plan(IReadOnlyCollection<DemTileKey> neededTiles)
    {
        ArgumentNullException.ThrowIfNull(neededTiles);

        var neededSet = new HashSet<DemTileKey>(neededTiles);

        var toLoad = new List<DemTileKey>();
        foreach (DemTileKey tile in neededTiles)
        {
            Touch(tile);
            if (resident.Add(tile))
            {
                toLoad.Add(tile);
            }
        }

        var toEvict = new List<DemTileKey>();
        // Walk least-recently-used first, evicting non-needed tiles until within budget.
        for (int i = 0; i < recency.Count && resident.Count > maxResidentTiles;)
        {
            DemTileKey candidate = recency[i];
            if (neededSet.Contains(candidate))
            {
                i++;
                continue;
            }

            recency.RemoveAt(i);
            resident.Remove(candidate);
            toEvict.Add(candidate);
        }

        return new DemTileResidencyPlan(toLoad, toEvict);
    }

    // Move a tile to the most-recently-used end of the recency list.
    private void Touch(DemTileKey tile)
    {
        int existing = recency.IndexOf(tile);
        if (existing >= 0)
        {
            recency.RemoveAt(existing);
        }

        recency.Add(tile);
    }
}