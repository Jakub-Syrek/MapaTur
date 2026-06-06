using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Terrain;

/// <summary>Progress of a region load: <see cref="Completed"/> of <see cref="Total"/> tiles fetched.</summary>
/// <param name="Completed">Tiles whose fetch has finished (whether they returned data or not).</param>
/// <param name="Total">Total tiles planned for the region.</param>
public readonly record struct RegionLoadProgress(int Completed, int Total);

/// <summary>
/// Loads a whole region's elevation as one <see cref="DemRaster"/> by planning the slippy tiles that
/// cover a <see cref="MapBounds"/> at a zoom (<see cref="DemTilePlanner"/>), fetching them through an
/// <see cref="IDemTileSource"/> (e.g. a composite of GUGiK NMT 1 m + global Terrarium) with bounded
/// concurrency, and stitching the results with <see cref="DemTileMosaic"/>. Tiles that come back null
/// (out of coverage / failed) are skipped; the region is null only when no tile could be fetched.
/// </summary>
public sealed class OnlineRegionDemLoader
{
    private readonly IDemTileSource source;
    private readonly int maxConcurrentFetches;

    /// <summary>Initializes the loader over a tile source.</summary>
    /// <param name="source">The DEM tile source (often a <see cref="CompositeDemTileSource"/>).</param>
    /// <param name="maxConcurrentFetches">Max tiles fetched at once. Bounded to stay a good citizen of
    /// the shared national WCS while still beating a one-at-a-time crawl. Default 6.</param>
    public OnlineRegionDemLoader(IDemTileSource source, int maxConcurrentFetches = 6)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (maxConcurrentFetches < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxConcurrentFetches), maxConcurrentFetches, "Concurrency must be at least 1.");
        }

        this.source = source;
        this.maxConcurrentFetches = maxConcurrentFetches;
    }

    /// <summary>
    /// Fetches and stitches the tiles covering <paramref name="bounds"/> at <paramref name="zoom"/>.
    /// Returns <c>null</c> when no tile was available.
    /// </summary>
    /// <param name="bounds">Geographic extent to cover.</param>
    /// <param name="zoom">Slippy zoom level to fetch at.</param>
    /// <param name="progress">Optional reporter; fires once per tile as fetches finish.</param>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public async Task<DemRaster?> LoadRegionAsync(
        MapBounds bounds,
        int zoom,
        IProgress<RegionLoadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DemTileKey> keys = DemTilePlanner.TilesForBounds(bounds, zoom);
        int total = keys.Count;

        using var gate = new SemaphoreSlim(this.maxConcurrentFetches);
        int completed = 0;
        var tasks = new List<Task<PlacedDemTile?>>(keys.Count);
        foreach (DemTileKey key in keys)
        {
            tasks.Add(FetchAsync(
                key,
                gate,
                () => progress?.Report(new RegionLoadProgress(Interlocked.Increment(ref completed), total)),
                cancellationToken));
        }

        PlacedDemTile?[] fetched = await Task.WhenAll(tasks).ConfigureAwait(false);

        var placed = new List<PlacedDemTile>(fetched.Length);
        foreach (PlacedDemTile? tile in fetched)
        {
            if (tile is not null)
            {
                placed.Add(tile);
            }
        }

        if (placed.Count == 0)
        {
            return null;
        }

        // Fill NoData (coverage gaps / a bbox clipping the Slovak border) so the mesh — which has no
        // NoData handling — extends flat to the edge instead of spiking to the sentinel depth.
        return DemRasterRepair.FillNoData(DemTileMosaic.Stitch(placed));
    }

    private async Task<PlacedDemTile?> FetchAsync(
        DemTileKey key, SemaphoreSlim gate, Action onFetched, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DemRaster? raster = await this.source.GetTileAsync(key, cancellationToken).ConfigureAwait(false);
            return raster is null ? null : new PlacedDemTile(key, raster);
        }
        finally
        {
            gate.Release();
            onFetched();
        }
    }
}