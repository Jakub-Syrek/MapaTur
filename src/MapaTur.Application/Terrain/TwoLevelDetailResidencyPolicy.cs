using MapaTur.Domain.Geography;

namespace MapaTur.Application.Terrain;

/// <summary>One detail level's inputs to <see cref="TwoLevelDetailResidencyPolicy"/>: its lattice, ring policy,
/// per-cell VRAM cost, a hard cap on resident cells, and an optional coverage predicate (ci,cj → the source tiles
/// exist). A null coverage predicate means every cell is available (no gating); a non-null one is how the fine
/// (5 cm) level is confined to where its tiles were actually fetched.</summary>
public sealed record DetailLevelSpec(
    OrthoDetailGrid Grid,
    OrthoDetailResidencyPolicy Policy,
    long CellBytes,
    int HardCap,
    Func<int, int, bool>? Coverage);

/// <summary>The cells each level should keep resident this frame — <paramref name="FineCells"/> = det05 (5 cm),
/// <paramref name="CoarseCells"/> = det25 (25 cm), both as grid cell keys.</summary>
public readonly record struct TwoLevelDesired(IReadOnlyList<int> FineCells, IReadOnlyList<int> CoarseCells);

/// <summary>
/// Coordinates two detail levels — det05 (fine) over det25 (coarse) — against ONE shared VRAM budget, finest-wins
/// (docs/PLAN-ortho-massif-streaming, R5). The FINE ring is funded FIRST (5 cm is the payoff tier) but only where
/// its coverage predicate says the source exists (the 07-14 map: 5 cm is a partial strip, not the whole core).
/// The COARSE ring takes the REMAINING budget; it is wider than the fine ring, so it always underlies it and is
/// the fallback wherever a fine cell is missing or not yet resident — the base ortho never shows through on a
/// level swap. Detail is optional: if the base ortho already fills the budget, BOTH rings come back empty instead
/// of forcing an over-commit (the shared-ledger rule). Pure decision logic — the GL streaming is elsewhere.
/// </summary>
public sealed class TwoLevelDetailResidencyPolicy
{
    private readonly DetailLevelSpec fine;
    private readonly DetailLevelSpec coarse;
    private readonly long totalBudgetBytes;
    private readonly int coarseBackingCells;

    /// <summary>Creates a two-level policy. <paramref name="fine"/> (det05) is prioritised for budget and should
    /// carry the coverage predicate; <paramref name="coarse"/> (det25) is the wider fallback tier.
    /// <paramref name="coarseBackingCells"/> is how many coarse cells the shared budget RESERVES before funding
    /// fine, so the fallback tier under the fine ring is never starved (the no-hole invariant): set it to the
    /// number of coarse cells that geometrically cover the fine ring (a 400 m fine ring under 1024 m coarse cells
    /// needs up to 2×2 = 4). Without the reserve, in the real regime (fine ≈4× the coarse cell size) fine would
    /// eat the whole budget and leave zero coarse cells under it — base showing through on a level swap.</summary>
    public TwoLevelDetailResidencyPolicy(
        DetailLevelSpec fine, DetailLevelSpec coarse, long totalBudgetBytes, int coarseBackingCells)
    {
        ArgumentNullException.ThrowIfNull(fine);
        ArgumentNullException.ThrowIfNull(coarse);
        ArgumentOutOfRangeException.ThrowIfNegative(coarseBackingCells);
        this.fine = fine;
        this.coarse = coarse;
        this.totalBudgetBytes = totalBudgetBytes;
        this.coarseBackingCells = coarseBackingCells;
    }

    /// <summary>
    /// The desired resident cells for both levels this frame, given the camera focus + velocity and the base
    /// ortho's already-resident bytes. The coarse fallback under the fine ring is reserved first, then det05 is
    /// funded from the remainder (gated to covered cells), then det25 takes what is left.
    /// </summary>
    public TwoLevelDesired Plan(GeoPoint focus, double velEastMps, double velNorthMps, long baseResidentBytes)
    {
        long free = Math.Max(0, this.totalBudgetBytes - baseResidentBytes);

        // Reserve budget for the coarse cells that BACK the fine ring, so 5 cm can never be funded without the
        // 25 cm fallback underneath it (finest-wins layers det05 OVER det25 — a fine cell with no coarse cell
        // under it shows bare base). Fine is still the priority; it is just funded from what remains after the
        // reserve, not by starving its own fallback.
        long coarseReserve = (long)Math.Min(this.coarseBackingCells, this.coarse.HardCap) * this.coarse.CellBytes;
        long freeForFine = Math.Max(0, free - coarseReserve);

        int fineCap = NearCap(freeForFine, this.fine.CellBytes, this.fine.HardCap);
        IReadOnlyList<int> fineCells = Desired(this.fine, focus, velEastMps, velNorthMps, fineCap);

        // COARSE takes the remaining budget after the fine cells actually kept; the reserve guarantees this leaves
        // at least coarseBackingCells for it whenever any fine cell was funded.
        long coarseFree = Math.Max(0, free - ((long)fineCells.Count * this.fine.CellBytes));
        int coarseCap = NearCap(coarseFree, this.coarse.CellBytes, this.coarse.HardCap);
        IReadOnlyList<int> coarseCells = Desired(this.coarse, focus, velEastMps, velNorthMps, coarseCap);

        return new TwoLevelDesired(fineCells, coarseCells);
    }

    // Cells that fit in the free budget at this cell size, clamped to the hard cap. 0 when a single cell does not
    // fit (detail is optional — never force an over-commit against the shared base+detail ledger).
    private static int NearCap(long freeBytes, long cellBytes, int hardCap)
    {
        if (cellBytes <= 0 || hardCap <= 0 || freeBytes < cellBytes)
        {
            return 0;
        }

        return (int)Math.Min(hardCap, freeBytes / cellBytes);
    }

    // The nearest-first ring for a level, coverage-FILTERED then capped (never the reverse): a coverage-gated
    // level requests the whole ranked ring, drops uncovered cells, and only then keeps the nearest `cap` — so a
    // covered cell just beyond a nearer hole still fills the budget instead of the ring silently under-filling.
    private static IReadOnlyList<int> Desired(DetailLevelSpec level, GeoPoint focus, double velE, double velN, int cap)
    {
        if (cap <= 0)
        {
            return Array.Empty<int>();
        }

        int requestCap = level.Coverage is null ? cap : int.MaxValue;
        IReadOnlyList<int> ranked = level.Policy.DesiredCells(focus, velE, velN, requestCap);
        IReadOnlyList<int> covered = Filter(level, ranked);
        return covered.Count <= cap ? covered : covered.Take(cap).ToList();
    }

    // Keep only cells the level's coverage predicate admits (null = all), preserving the ring's nearest-first
    // order. This is why a fine cell over a source hole is simply dropped (→ the coarse tier shows) instead of
    // composing a black/empty 5 cm cell.
    private static IReadOnlyList<int> Filter(DetailLevelSpec level, IReadOnlyList<int> desired)
    {
        if (level.Coverage is null || desired.Count == 0)
        {
            return desired;
        }

        var kept = new List<int>(desired.Count);
        foreach (int key in desired)
        {
            (int ci, int cj) = level.Grid.CellFromKey(key);
            if (level.Coverage(ci, cj))
            {
                kept.Add(key);
            }
        }

        return kept;
    }
}