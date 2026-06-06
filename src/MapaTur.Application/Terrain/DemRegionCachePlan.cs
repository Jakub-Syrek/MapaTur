namespace MapaTur.Application.Terrain;

/// <summary>
/// Splits the tiles a region needs into what is already on disk versus what still has to be fetched, the
/// primitive for offline-first 1 m coverage. GUGiK tiles persist after first load (the source caches each
/// <c>{z}/{x}/{y}.tif</c>), so over time the local cache accumulates: this plan lets a "download Tatras
/// offline" pass fetch only the <see cref="Missing"/> tiles (resumable, real % via <see cref="CoverageFraction"/>)
/// and lets an LOD layer answer "is this tile local?" before touching the network.
/// </summary>
/// <param name="Missing">Tiles not yet cached, in the order they appeared in the source list.</param>
/// <param name="CachedCount">How many of the region's tiles are already on disk.</param>
/// <param name="TotalCount">Total tiles the region requires.</param>
public sealed record DemRegionCachePlan(
    IReadOnlyList<DemTileKey> Missing,
    int CachedCount,
    int TotalCount)
{
    /// <summary>Fraction of the region already cached, in [0, 1]. An empty region counts as fully covered.</summary>
    public double CoverageFraction => this.TotalCount == 0 ? 1.0 : (double)this.CachedCount / this.TotalCount;

    /// <summary>True when nothing is left to fetch.</summary>
    public bool IsFullyCached => this.Missing.Count == 0;

    /// <summary>
    /// Builds a plan for <paramref name="tiles"/> using <paramref name="isCached"/> to probe each tile's
    /// on-disk presence (e.g. <c>File.Exists</c> over the tile-source cache, kept out of this layer).
    /// </summary>
    public static DemRegionCachePlan For(IReadOnlyList<DemTileKey> tiles, Func<DemTileKey, bool> isCached)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        ArgumentNullException.ThrowIfNull(isCached);

        var missing = new List<DemTileKey>();
        int cached = 0;
        foreach (DemTileKey tile in tiles)
        {
            if (isCached(tile))
            {
                cached++;
            }
            else
            {
                missing.Add(tile);
            }
        }

        return new DemRegionCachePlan(missing, cached, tiles.Count);
    }
}