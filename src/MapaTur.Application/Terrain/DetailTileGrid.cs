namespace MapaTur.Application.Terrain;

/// <summary>
/// Absolute-grid tiling for the 1 m detail plan. The default planner splits each loaded window into N equal
/// parts measured FROM THE WINDOW EDGE, so the same ground lands in a different tile every time the window
/// slides — the block keys then never match and the <see cref="PerTileMeshCache"/> can't reuse anything across
/// a camera move. These boundaries are instead placed on a FIXED absolute cell grid (multiples of
/// <c>quantum</c> in absolute z16-pixel space), so a given patch of ground keeps the same tile boundaries no
/// matter which window contains it → stable keys → cache hits while flying.
/// </summary>
public static class DetailTileGrid
{
    /// <summary>
    /// Local boundary columns/rows over <c>[0, total]</c> that fall on the absolute grid (multiples of
    /// <paramref name="quantum"/> once shifted by <paramref name="absOrigin"/>), always including the window
    /// ends 0 and <paramref name="total"/>. Each returned segment is at least 2 cells (a legal mesh crop):
    /// an aligned cut that would leave a &lt;2-cell sliver against an end is dropped.
    /// </summary>
    /// <param name="absOrigin">Absolute z16-pixel coordinate of local index 0 (the window's NW corner on this axis).</param>
    /// <param name="total">Window extent on this axis in cells (last local index).</param>
    /// <param name="quantum">Absolute grid pitch in cells (the target plan-tile size).</param>
    public static int[] AbsoluteBoundaries(int absOrigin, int total, int quantum)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(total);
        ArgumentOutOfRangeException.ThrowIfLessThan(quantum, 2);

        if (total < 4)
        {
            return new[] { 0, total }; // too small to split into two legal crops
        }

        var bounds = new List<int> { 0 };

        // First absolute multiple of quantum strictly after absOrigin, expressed in local coordinates.
        long firstMultiple = ((long)absOrigin / quantum + 1) * quantum;
        int local = (int)(firstMultiple - absOrigin);
        for (; local < total; local += quantum)
        {
            if (local >= 2 && total - local >= 2) // keep both sides ≥ 2 cells
            {
                bounds.Add(local);
            }
        }

        bounds.Add(total);
        return bounds.ToArray();
    }
}