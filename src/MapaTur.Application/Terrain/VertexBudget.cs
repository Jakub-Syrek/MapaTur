namespace MapaTur.Application.Terrain;

/// <summary>One tile's budget input: where it WANTS to be, how important it is, and what each level costs.</summary>
/// <param name="DesiredLevel">Index into <paramref name="VertexCostByLevel"/> the tile would render at (0 = finest).</param>
/// <param name="Priority">Higher = kept finer; the budget demotes the lowest-priority tiles first.</param>
/// <param name="VertexCostByLevel">Vertex count at each level, finest (index 0) to coarsest.</param>
public sealed record TileBudgetEntry(int DesiredLevel, double Priority, IReadOnlyList<long> VertexCostByLevel);

/// <summary>
/// Hard detail-vertex cap for the per-tile roughness LOD (Model 1): roughness may push many tiles toward HD,
/// but the total must stay within a budget so FPS can't blow up and a whole valley can't go HD. Every tile
/// starts at its desired level; while the total exceeds the budget the LOWEST-priority tiles are stepped down
/// one level at a time — sharp/near tiles keep their detail, smooth/far ones give it up first.
/// </summary>
public static class VertexBudget
{
    /// <summary>
    /// Returns the per-tile level (same order as <paramref name="tiles"/>) after constraining the total
    /// vertex count to <paramref name="maxVertices"/>. A tile is never promoted past its desired level; it is
    /// demoted only as far as its coarsest level, and the pass stops gracefully if nothing can demote further.
    /// </summary>
    public static IReadOnlyList<int> ConstrainToBudget(IReadOnlyList<TileBudgetEntry> tiles, long maxVertices)
    {
        ArgumentNullException.ThrowIfNull(tiles);

        var level = new int[tiles.Count];
        long total = 0;
        for (int i = 0; i < tiles.Count; i++)
        {
            int maxLevel = tiles[i].VertexCostByLevel.Count - 1;
            level[i] = Math.Clamp(tiles[i].DesiredLevel, 0, Math.Max(0, maxLevel));
            total += tiles[i].VertexCostByLevel[level[i]];
        }

        while (total > maxVertices)
        {
            int victim = -1;
            double lowestPriority = double.MaxValue;
            for (int i = 0; i < tiles.Count; i++)
            {
                int maxLevel = tiles[i].VertexCostByLevel.Count - 1;
                if (level[i] < maxLevel && tiles[i].Priority < lowestPriority)
                {
                    lowestPriority = tiles[i].Priority;
                    victim = i;
                }
            }

            if (victim < 0)
            {
                break; // everything is already at its coarsest — nothing left to give up
            }

            long before = tiles[victim].VertexCostByLevel[level[victim]];
            level[victim]++;
            total += tiles[victim].VertexCostByLevel[level[victim]] - before;
        }

        return level;
    }
}