namespace MapaTur.Application.Terrain;

/// <summary>
/// What the LOD streamer must do to move from the currently-resident detail tiles to the desired set as
/// the camera moves: <see cref="ToLoad"/> (start loading/building), <see cref="ToEvict"/> (free their GPU
/// buffers), <see cref="ToKeep"/> (already resident and still wanted).
/// </summary>
/// <param name="ToLoad">Desired tiles not yet resident.</param>
/// <param name="ToEvict">Resident tiles no longer desired.</param>
/// <param name="ToKeep">Tiles that are both resident and desired.</param>
public sealed record DetailTileResidencyPlan(
    IReadOnlyList<DemTileKey> ToLoad,
    IReadOnlyList<DemTileKey> ToEvict,
    IReadOnlyList<DemTileKey> ToKeep);

/// <summary>
/// Diffs the currently-resident detail tiles against the desired ring (<see cref="DetailTileRing"/>) to
/// tell the streamer what to load, evict and keep. Pure set arithmetic — the actual background load/build,
/// the double-buffered swap, and the per-frame GPU upload budget are the streamer's job. See the LOD
/// architecture plan.
/// </summary>
public static class DetailTileResidency
{
    /// <summary>Plans the load/evict/keep transition from <paramref name="resident"/> to <paramref name="desired"/>.</summary>
    public static DetailTileResidencyPlan Plan(
        IEnumerable<DemTileKey> resident, IEnumerable<DemTileKey> desired)
    {
        ArgumentNullException.ThrowIfNull(resident);
        ArgumentNullException.ThrowIfNull(desired);

        var residentSet = new HashSet<DemTileKey>(resident);
        var desiredSet = new HashSet<DemTileKey>(desired);

        var toLoad = new List<DemTileKey>();
        var toKeep = new List<DemTileKey>();
        foreach (DemTileKey tile in desiredSet)
        {
            (residentSet.Contains(tile) ? toKeep : toLoad).Add(tile);
        }

        var toEvict = new List<DemTileKey>();
        foreach (DemTileKey tile in residentSet)
        {
            if (!desiredSet.Contains(tile))
            {
                toEvict.Add(tile);
            }
        }

        return new DetailTileResidencyPlan(toLoad, toEvict, toKeep);
    }
}