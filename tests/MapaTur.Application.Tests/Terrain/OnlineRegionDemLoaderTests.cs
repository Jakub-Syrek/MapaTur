using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour pinning for <see cref="OnlineRegionDemLoader"/>: it plans the tiles for a region, pulls
/// each through an <see cref="IDemTileSource"/> with bounded concurrency, and stitches them into one
/// raster — skipping tiles the source can't supply and returning null only when none are available.
/// </summary>
public sealed class OnlineRegionDemLoaderTests
{
    private const int Zoom = 11;
    private static readonly MapBounds Region = new(new GeoPoint(49.1, 19.9), new GeoPoint(49.3, 20.1));

    private static DemRaster TileRaster(DemTileKey key, float fill = 1f)
    {
        var (west, south, east, north) = SlippyTileMath.TileBounds(key.X, key.Y, key.Zoom);
        var bounds = new MapBounds(new GeoPoint(south, west), new GeoPoint(north, east));
        var samples = new float[4];
        Array.Fill(samples, fill);
        return new DemRaster(2, 2, bounds, samples);
    }

    private sealed class FakeSource : IDemTileSource
    {
        private readonly Func<DemTileKey, DemRaster?> factory;
        private readonly object sync = new();
        private readonly List<DemTileKey> requested = new();

        public FakeSource(Func<DemTileKey, DemRaster?> factory) => this.factory = factory;

        // Snapshot — fetches run concurrently, so the backing list is guarded.
        public IReadOnlyList<DemTileKey> Requested
        {
            get { lock (this.sync) { return this.requested.ToList(); } }
        }

        public Task<DemRaster?> GetTileAsync(DemTileKey key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (this.sync)
            {
                this.requested.Add(key);
            }

            return Task.FromResult(this.factory(key));
        }
    }

    // Tracks how many fetches are in flight at once so the concurrency cap can be asserted.
    private sealed class ConcurrencyTrackingSource : IDemTileSource
    {
        private readonly object sync = new();
        private int current;

        public int MaxObserved { get; private set; }

        public async Task<DemRaster?> GetTileAsync(DemTileKey key, CancellationToken cancellationToken = default)
        {
            lock (this.sync)
            {
                this.current++;
                if (this.current > this.MaxObserved)
                {
                    this.MaxObserved = this.current;
                }
            }

            try
            {
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
                return TileRaster(key);
            }
            finally
            {
                lock (this.sync)
                {
                    this.current--;
                }
            }
        }
    }

    [Fact]
    public async Task LoadRegionAsync_RequestsEveryPlannedTile()
    {
        var expected = DemTilePlanner.TilesForBounds(Region, Zoom);
        var source = new FakeSource(k => TileRaster(k));
        var loader = new OnlineRegionDemLoader(source);

        await loader.LoadRegionAsync(Region, Zoom);

        source.Requested.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task LoadRegionAsync_StitchesTilesIntoOneRaster()
    {
        var expected = DemTilePlanner.TilesForBounds(Region, Zoom);
        int gridCols = expected.Max(k => k.X) - expected.Min(k => k.X) + 1;
        int gridRows = expected.Max(k => k.Y) - expected.Min(k => k.Y) + 1;
        var loader = new OnlineRegionDemLoader(new FakeSource(k => TileRaster(k)));

        DemRaster? mosaic = await loader.LoadRegionAsync(Region, Zoom);

        mosaic.Should().NotBeNull();
        mosaic!.Columns.Should().Be(gridCols * 2);
        mosaic.Rows.Should().Be(gridRows * 2);
    }

    [Fact]
    public async Task LoadRegionAsync_ReturnsNull_WhenNoTileIsAvailable()
    {
        var loader = new OnlineRegionDemLoader(new FakeSource(_ => null));

        DemRaster? mosaic = await loader.LoadRegionAsync(Region, Zoom);

        mosaic.Should().BeNull();
    }

    [Fact]
    public async Task LoadRegionAsync_SkipsUnavailableTiles_AndStitchesTheRest()
    {
        var planned = DemTilePlanner.TilesForBounds(Region, Zoom);
        DemTileKey drop = planned[0];
        var loader = new OnlineRegionDemLoader(new FakeSource(k => k.Equals(drop) ? null : TileRaster(k)));

        DemRaster? mosaic = await loader.LoadRegionAsync(Region, Zoom);

        mosaic.Should().NotBeNull("the remaining tiles still form a region");
    }

    [Fact]
    public async Task LoadRegionAsync_LimitsConcurrentFetches()
    {
        var source = new ConcurrencyTrackingSource();
        var loader = new OnlineRegionDemLoader(source, maxConcurrentFetches: 2);

        await loader.LoadRegionAsync(Region, 13);

        source.MaxObserved.Should().BeLessThanOrEqualTo(2, "the loader caps in-flight fetches");
        source.MaxObserved.Should().BeGreaterThan(1, "fetches should overlap, not run one-at-a-time");
    }

    // Synchronous IProgress so the loader's reports land before LoadRegionAsync returns (the default
    // Progress<T> posts to a SynchronizationContext, which a unit test doesn't have — making it racy).
    private sealed class SyncProgress : IProgress<RegionLoadProgress>
    {
        private readonly object sync = new();
        private readonly List<RegionLoadProgress> reports = new();

        public IReadOnlyList<RegionLoadProgress> Reports
        {
            get { lock (this.sync) { return this.reports.ToList(); } }
        }

        public void Report(RegionLoadProgress value)
        {
            lock (this.sync)
            {
                this.reports.Add(value);
            }
        }
    }

    [Fact]
    public async Task LoadRegionAsync_ReportsProgress_ForEveryPlannedTile()
    {
        var planned = DemTilePlanner.TilesForBounds(Region, Zoom);
        var loader = new OnlineRegionDemLoader(new FakeSource(k => TileRaster(k)));
        var progress = new SyncProgress();

        await loader.LoadRegionAsync(Region, Zoom, progress);

        IReadOnlyList<RegionLoadProgress> reports = progress.Reports;
        reports.Should().HaveCount(planned.Count, "one report fires per tile fetched");
        reports.Should().OnlyContain(p => p.Total == planned.Count, "every report carries the planned total");
        reports.Max(p => p.Completed).Should().Be(planned.Count, "the final report signals all tiles done");
    }

    [Fact]
    public void Constructor_RejectsNonPositiveConcurrency()
    {
        var act = () => new OnlineRegionDemLoader(new FakeSource(_ => null), maxConcurrentFetches: 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task LoadRegionAsync_HonoursCancellation_BeforeFetching()
    {
        var source = new FakeSource(k => TileRaster(k));
        var loader = new OnlineRegionDemLoader(source);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await loader.LoadRegionAsync(Region, Zoom, cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        source.Requested.Should().BeEmpty();
    }
}