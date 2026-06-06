using MapaTur.Domain.Geography;

namespace MapaTur.Application.Terrain;

/// <summary>Progress of an offline region download: <see cref="Completed"/> of <see cref="Total"/> tiles
/// processed (cached + downloaded + failed), with a running <see cref="Failed"/> count.</summary>
public readonly record struct OfflineDownloadProgress(int Completed, int Total, int Failed);

/// <summary>Outcome of an offline region download.</summary>
/// <param name="Total">Tiles the region needs.</param>
/// <param name="AlreadyCached">Tiles already on disk before the run (skipped).</param>
/// <param name="Downloaded">Tiles fetched and cached this run.</param>
/// <param name="Failed">Missing tiles whose fetch returned no data (left uncached for a later resume).</param>
public readonly record struct OfflineDownloadResult(int Total, int AlreadyCached, int Downloaded, int Failed);

/// <summary>
/// Pre-fills the on-disk tile cache for a whole region so the app works offline in the field — there is
/// no signal on the ridge, so the user downloads the lot over WiFi first. Plans the region's tiles
/// (<see cref="DemTilePlanner"/>), skips whatever is already cached (<see cref="DemRegionCachePlan"/> over an
/// injected probe) so a dropped connection just resumes, and fetches the rest through an
/// <see cref="IDemTileSource"/> (which caches each as a side effect) with bounded concurrency. A tile that
/// comes back null is counted as failed and left uncached, so a re-run picks it up.
/// </summary>
public sealed class OfflineRegionDownloader
{
    private readonly IDemTileSource source;
    private readonly Func<DemTileKey, bool> isCached;
    private readonly int maxConcurrentFetches;

    /// <summary>Initializes the downloader.</summary>
    /// <param name="source">Tile source that caches each fetched tile (e.g. GUGiK NMT 1 m).</param>
    /// <param name="isCached">Probe for whether a tile is already on disk (e.g. the source's IsCached).</param>
    /// <param name="maxConcurrentFetches">Max tiles fetched at once. Default 4 — gentle on the shared
    /// national WCS during a large bulk pull while still beating a one-at-a-time crawl.</param>
    public OfflineRegionDownloader(IDemTileSource source, Func<DemTileKey, bool> isCached, int maxConcurrentFetches = 4)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(isCached);
        if (maxConcurrentFetches < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxConcurrentFetches), maxConcurrentFetches, "Concurrency must be at least 1.");
        }

        this.source = source;
        this.isCached = isCached;
        this.maxConcurrentFetches = maxConcurrentFetches;
    }

    /// <summary>
    /// Downloads every not-yet-cached tile covering <paramref name="bounds"/> at <paramref name="zoom"/>.
    /// </summary>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public async Task<OfflineDownloadResult> DownloadAsync(
        MapBounds bounds,
        int zoom,
        IProgress<OfflineDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<DemTileKey> all = DemTilePlanner.TilesForBounds(bounds, zoom);
        DemRegionCachePlan plan = DemRegionCachePlan.For(all, this.isCached);

        int total = plan.TotalCount;
        int completed = plan.CachedCount; // cached tiles are already "done"
        int downloaded = 0;
        int failed = 0;
        progress?.Report(new OfflineDownloadProgress(completed, total, failed));

        using var gate = new SemaphoreSlim(this.maxConcurrentFetches);
        object counterLock = new();
        var tasks = new List<Task>(plan.Missing.Count);
        foreach (DemTileKey key in plan.Missing)
        {
            tasks.Add(FetchAsync(key));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return new OfflineDownloadResult(total, plan.CachedCount, downloaded, failed);

        async Task FetchAsync(DemTileKey key)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                bool ok = await this.source.GetTileAsync(key, cancellationToken).ConfigureAwait(false) is not null;
                lock (counterLock)
                {
                    if (ok)
                    {
                        downloaded++;
                    }
                    else
                    {
                        failed++;
                    }

                    completed++;
                    progress?.Report(new OfflineDownloadProgress(completed, total, failed));
                }
            }
            finally
            {
                gate.Release();
            }
        }
    }
}