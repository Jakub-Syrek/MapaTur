namespace MapaTur.Application.Terrain;

public static class RockDemCoveragePlanner
{
    public static IReadOnlySet<DemTileKey> ExpandWithHalo(
        IEnumerable<DemTileKey> candidates,
        IEnumerable<DemTileKey> available,
        int haloTiles)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(available);
        ArgumentOutOfRangeException.ThrowIfNegative(haloTiles);

        HashSet<DemTileKey> availableSet = available.ToHashSet();
        var result = new HashSet<DemTileKey>();
        foreach (DemTileKey candidate in candidates)
        {
            for (int offsetY = -haloTiles; offsetY <= haloTiles; offsetY++)
            {
                for (int offsetX = -haloTiles; offsetX <= haloTiles; offsetX++)
                {
                    var neighbour = new DemTileKey(
                        candidate.Zoom,
                        candidate.X + offsetX,
                        candidate.Y + offsetY);
                    if (availableSet.Contains(neighbour))
                    {
                        result.Add(neighbour);
                    }
                }
            }
        }

        return result;
    }
}
