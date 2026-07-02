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
        public List<DemTileKey> Requested
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

    [Fact]
    public async Task LoadRegionAsync_WithTileAvailablePredicate_NeverFetchesUnavailableTiles()
    {
        // Offline-deterministic render loop: only tiles the predicate accepts (e.g. already cached) may be
        // fetched — the rest must never reach the source, so no WCS download is triggered while flying.
        var planned = DemTilePlanner.TilesForBounds(Region, Zoom);
        DemTileKey available = planned[0];
        var source = new FakeSource(k => TileRaster(k));
        var loader = new OnlineRegionDemLoader(source);

        await loader.LoadRegionAsync(Region, Zoom, tileAvailable: key => key.Equals(available));

        source.Requested.Should().ContainSingle().Which.Should().Be(available);
    }

    [Fact]
    public async Task LoadRegionAsync_WithTileAvailablePredicate_ReturnsNull_AndFetchesNothing_WhenNoneAvailable()
    {
        var source = new FakeSource(k => TileRaster(k));
        var loader = new OnlineRegionDemLoader(source);

        DemRaster? mosaic = await loader.LoadRegionAsync(Region, Zoom, tileAvailable: _ => false);

        mosaic.Should().BeNull("nothing cached ⇒ no layer; the caller leaves the base showing");
        source.Requested.Should().BeEmpty("the render loop never reaches out to the network");
    }

    // Step B (decoded-tile cache). A per-key fill so a wrong reused tile would change the stitched samples — the
    // identical-output assertions below would catch a cache that handed back the wrong tile.
    private static DemRaster KeyedTileRaster(DemTileKey key)
        => TileRaster(key, fill: (key.X * 1000) + key.Y);

    // Two regions one tile apart, so the second overlaps the first by all-but-one column of tiles.
    private static readonly MapBounds RegionA = new(new GeoPoint(49.10, 19.90), new GeoPoint(49.30, 20.10));
    private static readonly MapBounds RegionB = new(new GeoPoint(49.10, 20.05), new GeoPoint(49.30, 20.25));

    private static void AssertSameSamples(DemRaster a, DemRaster b)
    {
        a.Columns.Should().Be(b.Columns);
        a.Rows.Should().Be(b.Rows);
        a.Samples.Should().Equal(b.Samples, "the cached mosaic must be byte-for-byte identical to a fresh stitch");
    }

    [Fact]
    public async Task LoadRegionAsync_WithDecodedTileCache_OnSecondLoad_OnlyDecodesNewlyEnteredTiles()
    {
        var planA = DemTilePlanner.TilesForBounds(RegionA, Zoom).ToHashSet();
        var planB = DemTilePlanner.TilesForBounds(RegionB, Zoom).ToHashSet();
        int overlap = planA.Count(planB.Contains);
        overlap.Should().BeGreaterThan(0, "the two regions must share tiles for the test to mean anything");
        int newInB = planB.Count(k => !planA.Contains(k));

        var source = new FakeSource(KeyedTileRaster);
        var loader = new OnlineRegionDemLoader(source, cacheDecodedTiles: true);

        await loader.LoadRegionAsync(RegionA, Zoom); // warm the cache
        int afterA = source.Requested.Count;
        afterA.Should().Be(planA.Count, "the first load decodes every planned tile");

        await loader.LoadRegionAsync(RegionB, Zoom);
        int decodedForB = source.Requested.Count - afterA;

        decodedForB.Should().Be(newInB, "only the newly-entered tiles are decoded; the overlap is reused");
        loader.LastDecodedTileHits.Should().Be(overlap, "the overlapping tiles are cache hits");
        loader.LastDecodedTileMisses.Should().Be(newInB);
    }

    [Fact]
    public async Task LoadRegionAsync_WithDecodedTileCache_ShiftedWindow_YieldsIdenticalRasterToFreshStitch()
    {
        // The cached loader (after a warm A→B) must produce the SAME B raster as a brand-new loader stitching B
        // from scratch — proving the decoded-tile cache is a pure speed optimization, identical pixels out.
        var cachedLoader = new OnlineRegionDemLoader(new FakeSource(KeyedTileRaster), cacheDecodedTiles: true);
        await cachedLoader.LoadRegionAsync(RegionA, Zoom);
        DemRaster? cachedB = await cachedLoader.LoadRegionAsync(RegionB, Zoom);

        var freshLoader = new OnlineRegionDemLoader(new FakeSource(KeyedTileRaster), cacheDecodedTiles: false);
        DemRaster? freshB = await freshLoader.LoadRegionAsync(RegionB, Zoom);

        cachedB.Should().NotBeNull();
        freshB.Should().NotBeNull();
        AssertSameSamples(cachedB!, freshB!);
    }

    [Fact]
    public async Task LoadRegionAsync_WithDecodedTileCache_WarmReload_ReusesEverything_AndMatchesColdRaster()
    {
        var source = new FakeSource(KeyedTileRaster);
        var loader = new OnlineRegionDemLoader(source, cacheDecodedTiles: true);

        DemRaster? cold = await loader.LoadRegionAsync(RegionA, Zoom);
        int afterCold = source.Requested.Count;

        DemRaster? warm = await loader.LoadRegionAsync(RegionA, Zoom); // identical window again
        source.Requested.Count.Should().Be(afterCold, "a reload of the same window decodes nothing new");
        loader.LastDecodedTileMisses.Should().Be(0, "every tile is already cached");
        loader.LastDecodedTileHits.Should().Be(DemTilePlanner.TilesForBounds(RegionA, Zoom).Count);

        AssertSameSamples(warm!, cold!);
    }

    [Fact]
    public async Task LoadRegionAsync_WithDecodedTileCache_EvictsTilesNotInTheCurrentWindow()
    {
        // Roam from A to a disjoint far region: the cache must not keep A's tiles forever (zoom-scoped eviction
        // drops the unused same-zoom tiles), so memory stays bounded to roughly one window.
        var farRegion = new MapBounds(new GeoPoint(50.10, 21.00), new GeoPoint(50.30, 21.20));
        var planA = DemTilePlanner.TilesForBounds(RegionA, Zoom);
        var planFar = DemTilePlanner.TilesForBounds(farRegion, Zoom);
        planA.Any(planFar.Contains).Should().BeFalse("the regions must be disjoint for this test");

        var loader = new OnlineRegionDemLoader(new FakeSource(KeyedTileRaster), cacheDecodedTiles: true);
        await loader.LoadRegionAsync(RegionA, Zoom);
        await loader.LoadRegionAsync(farRegion, Zoom);

        loader.DecodedTileCount.Should().Be(planFar.Count, "A's tiles are evicted once the window no longer covers them");
    }

    [Fact]
    public async Task LoadRegionAsync_WithDecodedTileCache_DoesNotEvictTilesAtADifferentZoom()
    {
        // The one singleton loader serves both the z16 detail re-center and the rarer base/legacy loads at other
        // zooms. A load at one zoom must NOT evict cached tiles at another zoom, or the two would thrash each other.
        var loader = new OnlineRegionDemLoader(new FakeSource(KeyedTileRaster), cacheDecodedTiles: true);
        await loader.LoadRegionAsync(RegionA, 13); // base-ish zoom
        int baseTiles = DemTilePlanner.TilesForBounds(RegionA, 13).Count;
        loader.DecodedTileCount.Should().Be(baseTiles);

        await loader.LoadRegionAsync(RegionA, 16); // detail zoom — different tiles, different zoom
        int detailTiles = DemTilePlanner.TilesForBounds(RegionA, 16).Count;

        loader.DecodedTileCount.Should().Be(baseTiles + detailTiles,
            "tiles at the other zoom survive a load that doesn't request that zoom");
    }

    [Fact]
    public async Task LoadRegionAsync_WithoutDecodedTileCache_DecodesEveryTileEveryLoad()
    {
        // Default (cache off): behaviour is unchanged — every load re-fetches every tile (no retained state).
        var source = new FakeSource(KeyedTileRaster);
        var loader = new OnlineRegionDemLoader(source);

        await loader.LoadRegionAsync(RegionA, Zoom);
        int afterFirst = source.Requested.Count;
        await loader.LoadRegionAsync(RegionA, Zoom);

        (source.Requested.Count - afterFirst).Should().Be(afterFirst, "with caching off, the same window re-fetches fully");
        loader.DecodedTileCount.Should().Be(0, "caching off ⇒ nothing retained");
    }
}