using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour pinning for <see cref="OfflineRegionDownloader"/>: it pre-fills the tile cache for a whole
/// region so the app works offline in the field (no signal in the mountains → download the lot on WiFi
/// first). It must be RESUMABLE — a dropped connection just re-runs and skips what is already on disk
/// (via the injected cache probe) — report progress, and survive individual tile failures.
/// </summary>
public sealed class OfflineRegionDownloaderTests
{
    private const int Zoom = 11;
    private static readonly MapBounds Region = new(new GeoPoint(49.1, 19.9), new GeoPoint(49.3, 20.1));

    private static DemRaster TileRaster(DemTileKey key)
    {
        var (west, south, east, north) = SlippyTileMath.TileBounds(key.X, key.Y, key.Zoom);
        var bounds = new MapBounds(new GeoPoint(south, west), new GeoPoint(north, east));
        return new DemRaster(2, 2, bounds, new float[4]);
    }

    // Records which tiles were actually fetched; returns null for keys in the "fail" set.
    private sealed class RecordingSource : IDemTileSource
    {
        private readonly object sync = new();
        private readonly HashSet<DemTileKey> fail;
        private readonly List<DemTileKey> fetched = new();

        public RecordingSource(IEnumerable<DemTileKey>? fail = null) => this.fail = fail is null ? new() : new(fail);

        public IReadOnlyList<DemTileKey> Fetched
        {
            get { lock (this.sync) { return this.fetched.ToList(); } }
        }

        public Task<DemRaster?> GetTileAsync(DemTileKey key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (this.sync)
            {
                this.fetched.Add(key);
            }

            return Task.FromResult<DemRaster?>(this.fail.Contains(key) ? null : TileRaster(key));
        }
    }

    [Fact]
    public async Task DownloadAsync_FetchesEveryMissingTile_AndSkipsCachedOnes()
    {
        var all = DemTilePlanner.TilesForBounds(Region, Zoom);
        var alreadyCached = new HashSet<DemTileKey> { all[0], all[1] };
        var source = new RecordingSource();
        var downloader = new OfflineRegionDownloader(source, alreadyCached.Contains);

        OfflineDownloadResult result = await downloader.DownloadAsync(Region, Zoom);

        source.Fetched.Should().NotContain(alreadyCached, "cached tiles must not be re-downloaded");
        source.Fetched.Should().HaveCount(all.Count - alreadyCached.Count);
        result.Total.Should().Be(all.Count);
        result.AlreadyCached.Should().Be(2);
        result.Downloaded.Should().Be(all.Count - 2);
        result.Failed.Should().Be(0);
    }

    [Fact]
    public async Task DownloadAsync_ReportsProgress_FromCachedBaseline_ReachingTotal()
    {
        var all = DemTilePlanner.TilesForBounds(Region, Zoom);
        var cached = new HashSet<DemTileKey> { all[0] };
        var source = new RecordingSource();
        var downloader = new OfflineRegionDownloader(source, cached.Contains);
        var reports = new List<OfflineDownloadProgress>();
        var progress = new SyncProgress<OfflineDownloadProgress>(reports.Add);

        await downloader.DownloadAsync(Region, Zoom, progress);

        reports.Should().NotBeEmpty();
        reports.Should().OnlyContain(p => p.Total == all.Count);
        reports.Max(p => p.Completed).Should().Be(all.Count, "the last report counts every tile as done");
    }

    [Fact]
    public async Task DownloadAsync_CountsFailedTiles_WhenTheSourceReturnsNull()
    {
        var all = DemTilePlanner.TilesForBounds(Region, Zoom);
        var source = new RecordingSource(fail: new[] { all[2], all[3] });
        var downloader = new OfflineRegionDownloader(source, _ => false);

        OfflineDownloadResult result = await downloader.DownloadAsync(Region, Zoom);

        result.Failed.Should().Be(2);
        result.Downloaded.Should().Be(all.Count - 2);
    }

    [Fact]
    public async Task DownloadAsync_HonoursCancellation_BeforeFetching()
    {
        var source = new RecordingSource();
        var downloader = new OfflineRegionDownloader(source, _ => false);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await downloader.DownloadAsync(Region, Zoom, cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        source.Fetched.Should().BeEmpty();
    }

    // Synchronous IProgress so reports land before DownloadAsync returns (Progress<T> posts to a
    // SynchronizationContext a unit test doesn't have).
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> sink;
        private readonly object gate = new();

        public SyncProgress(Action<T> sink) => this.sink = sink;

        public void Report(T value)
        {
            lock (this.gate)
            {
                this.sink(value);
            }
        }
    }
}