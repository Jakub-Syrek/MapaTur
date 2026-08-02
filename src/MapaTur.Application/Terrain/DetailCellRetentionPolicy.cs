namespace MapaTur.Application.Terrain;

/// <summary>
/// How many resident detail cells (5 cm / 25 cm ortho) must be released this frame.
/// <para>
/// Measured 2026-08-02 over the Roháče, where the view asked for <b>zero</b> 5 cm cells: the pool still
/// held all 192 of them, ~7.5 GB of a 16 GB card, for minutes. Eviction fired only on two conditions —
/// the pool exceeding its hard cap, or a desired cell starved of a free layer — and over a region that
/// wants no fine detail BOTH are zero, so nothing was ever released. The card stayed full of ground the
/// camera had left behind, which is what made a region without any 5 cm coverage stutter.
/// </para>
/// <para>
/// The third reason added here is staleness: a cell absent from the desired set for a long time is
/// released even under no pressure. Capacity is unchanged (the cap stays exactly what it was) and no
/// visible pixel changes — a stale cell is by definition one the view is not asking for.
/// </para>
/// </summary>
public static class DetailCellRetentionPolicy
{
    /// <summary>
    /// Ticks (frames in which the residency ran) a cell may stay off the desired set before it is stale.
    /// Long enough that panning back and forth, or a cell flickering at a ring boundary, never thrashes.
    /// </summary>
    public const long StaleAfterTicks = 300;

    /// <summary>Whether a cell absent this long from the desired set may be released under no pressure.</summary>
    public static bool IsStale(long ticksSinceDesired) => ticksSinceDesired > StaleAfterTicks;

    /// <summary>
    /// Cells to evict this frame: the LARGEST of the three reasons, never their sum (the sets overlap, and
    /// summing them would evict ground the camera is still using).
    /// </summary>
    /// <param name="residentCells">Cells currently held.</param>
    /// <param name="hardCapCells">Hard cap on resident cells (capacity — unchanged by this policy).</param>
    /// <param name="starvedCells">Desired cells still without an array layer.</param>
    /// <param name="freeLayers">Array layers currently free.</param>
    /// <param name="staleCells">Resident cells long absent from the desired set (see <see cref="IsStale"/>).</param>
    public static int EvictionCount(
        int residentCells, int hardCapCells, int starvedCells, int freeLayers, int staleCells)
    {
        int overCap = residentCells - Math.Max(0, hardCapCells);
        int starving = starvedCells - freeLayers;
        return Math.Max(0, Math.Max(overCap, Math.Max(starving, staleCells)));
    }
}