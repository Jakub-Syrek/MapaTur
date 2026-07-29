namespace MapaTur.Application.Terrain;

public static class RockDemTileBatchPlanner
{
    public static IReadOnlyList<IReadOnlyList<DemTileKey>> CreateContiguousRowBatches(
        IEnumerable<DemTileKey> keys,
        int maximumTilesPerBatch)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumTilesPerBatch, 1);

        DemTileKey[] ordered = keys
            .Distinct()
            .OrderBy(key => key.Y)
            .ThenBy(key => key.X)
            .ToArray();
        var batches = new List<IReadOnlyList<DemTileKey>>();
        var current = new List<DemTileKey>(maximumTilesPerBatch);
        foreach (DemTileKey key in ordered)
        {
            bool continuesCurrent = current.Count > 0
                && current[^1].Zoom == key.Zoom
                && current[^1].Y == key.Y
                && current[^1].X + 1 == key.X
                && current.Count < maximumTilesPerBatch;
            if (!continuesCurrent && current.Count > 0)
            {
                batches.Add(current.ToArray());
                current.Clear();
            }

            current.Add(key);
        }

        if (current.Count > 0)
        {
            batches.Add(current.ToArray());
        }

        return batches;
    }
}
