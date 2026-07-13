namespace MapaTur.Application.Terrain;

/// <summary>
/// Pure VRAM-budget arithmetic for the ortho residency planner: how many equally-sized ortho
/// cells (each an RGBA8 texture plus its mip chain) fit in a byte budget, clamped to the number
/// of cells that actually exist. Extracted from the GL renderer so the "all bundled cells stay
/// resident" guarantee is unit-testable without a GL context.
/// </summary>
public static class OrthoVramBudget
{
    /// <summary>
    /// Resident bytes one RGBA8 cell of the given pixel size needs, including a ~33% mip chain
    /// (1 + 1/4 + 1/16 + … ≈ 4/3 of the base level).
    /// </summary>
    public static long CellResidentBytes(int width, int height)
    {
        long bytes = (long)Math.Max(1, width) * Math.Max(1, height) * 4L;
        return bytes + (bytes / 3L);
    }

    /// <summary>
    /// Maximum cells that fit in <paramref name="budgetBytes"/> when the largest cell needs
    /// <paramref name="perCellBytes"/>, clamped to [1, <paramref name="cellCount"/>]. Always keeps
    /// at least one cell resident when any exist (you can't render a hole), returns 0 for no cells,
    /// and returns the full <paramref name="cellCount"/> when the per-cell size is unknown (≤ 0).
    /// </summary>
    public static int MaxResidentCells(long perCellBytes, int cellCount, long budgetBytes)
    {
        if (cellCount <= 0)
        {
            return 0;
        }

        if (perCellBytes <= 0)
        {
            return cellCount;
        }

        long fit = budgetBytes / perCellBytes;
        int budget = (int)Math.Min(cellCount, Math.Max(1L, fit));
        return Math.Max(1, budget);
    }

    /// <summary>
    /// How many hi-res DETAIL cells fit in the shared VRAM budget AFTER the base ortho's resident bytes,
    /// clamped to [0, <paramref name="hardCap"/>]. Unlike <see cref="MaxResidentCells"/> this returns 0 when
    /// the base already fills the budget: the detail overlay is OPTIONAL (the base ortho carries the frame),
    /// so it must never force an over-budget transient by insisting on a cell. Base + detail share ONE budget
    /// (the design-panel correction — the 8 base cells are ~1.9 GB, not 1.4; two independent planners would
    /// silently overcommit).
    /// </summary>
    public static int SharedDetailNearCap(
        long baseResidentBytes, long detailCellBytes, long totalBudgetBytes, int hardCap)
    {
        if (detailCellBytes <= 0 || hardCap <= 0)
        {
            return 0;
        }

        long free = totalBudgetBytes - baseResidentBytes;
        if (free < detailCellBytes)
        {
            return 0;
        }

        return (int)Math.Min(hardCap, free / detailCellBytes);
    }
}