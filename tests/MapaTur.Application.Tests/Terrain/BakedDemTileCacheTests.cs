using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour of <see cref="BakedDemTileCache"/>: a bounded, thread-safe LRU that sits in FRONT of the baked-tile
/// disk loader so a tile evicted from the streaming manager and later re-requested comes back from RAM instead of
/// re-reading (and re-deserialising) its <c>.bdt</c> — the "zwiedzanie całych Tatr bez reloadu z dysku" fix. The
/// disk loader is injected, so every test asserts on how many times the SOURCE (disk) was actually consulted.
/// </summary>
public sealed class BakedDemTileCacheTests
{
    private static readonly MapBounds AnyBounds =
        new(new GeoPoint(49.2, 20.0), new GeoPoint(49.3, 20.1));

    // A baked tile whose geometry is a known size, so a test can size the byte budget in whole-tile units.
    private static BakedDemTile TileOfCells(DemTileKey key, int cells)
    {
        var heights = new float[cells];
        for (int i = 0; i < heights.Length; i++)
        {
            heights[i] = 1000f + i;
        }

        return new BakedDemTile(key.Zoom, key.X, key.Y, cells, 1, AnyBounds, -9999.0, heights);
    }

    private static DemTileKey Key(int x) => new(16, x, 0);

    [Fact]
    public void Miss_LoadsFromSource_AndReturnsTheTile()
    {
        var cache = new BakedDemTileCache(k => TileOfCells(k, 16), maxBytes: 1L << 30);

        BakedDemTile? tile = cache.Load(Key(1));

        tile.Should().NotBeNull();
        tile!.Columns.Should().Be(16);
        cache.Misses.Should().Be(1);
        cache.Hits.Should().Be(0);
        cache.Count.Should().Be(1);
    }

    [Fact]
    public void Hit_SecondLoadOfSameKey_DoesNotConsultTheSource()
    {
        int sourceCalls = 0;
        var cache = new BakedDemTileCache(
            k => { sourceCalls++; return TileOfCells(k, 16); }, maxBytes: 1L << 30);

        BakedDemTile? first = cache.Load(Key(1));
        BakedDemTile? second = cache.Load(Key(1));

        sourceCalls.Should().Be(1, "the second load is served from RAM, not disk");
        second.Should().BeSameAs(first, "the cache hands back the identical cached instance");
        cache.Hits.Should().Be(1);
        cache.Misses.Should().Be(1);
    }

    [Fact]
    public void NullFromSource_IsNotCached_SoTheTileCanBeRetried()
    {
        // A missing/corrupt tile comes back null; caching the absence would make a transient read failure permanent.
        int sourceCalls = 0;
        BakedDemTile? Source(DemTileKey k)
        {
            sourceCalls++;
            return sourceCalls == 1 ? null : TileOfCells(k, 16); // fails once, then succeeds
        }

        var cache = new BakedDemTileCache(Source, maxBytes: 1L << 30);

        cache.Load(Key(1)).Should().BeNull();
        cache.Count.Should().Be(0, "a null result is not stored");
        cache.Load(Key(1)).Should().NotBeNull("the retry hits disk again and now succeeds");
        sourceCalls.Should().Be(2);
        cache.Count.Should().Be(1);
    }

    [Fact]
    public void OverByteBudget_EvictsLeastRecentlyUsedFirst()
    {
        // Each tile is 16 cells × 4 bytes = 64 B of geometry; a 2-tile budget forces eviction on the third insert.
        long twoTiles = 2 * 16L * sizeof(float);
        var cache = new BakedDemTileCache(k => TileOfCells(k, 16), maxBytes: twoTiles);

        cache.Load(Key(1));
        cache.Load(Key(2));
        cache.Load(Key(3)); // over budget → evicts the oldest, Key(1)

        cache.Count.Should().Be(2);
        cache.ResidentBytes.Should().BeLessThanOrEqualTo(twoTiles);

        int sourceCallsForOne = 0;
        var probe = new BakedDemTileCache(
            k => { if (k == Key(1)) { sourceCallsForOne++; } return TileOfCells(k, 16); }, maxBytes: twoTiles);
        probe.Load(Key(1));
        probe.Load(Key(2));
        probe.Load(Key(3));
        probe.Load(Key(1)); // was evicted → must hit disk again
        sourceCallsForOne.Should().Be(2, "Key(1) was evicted as least-recently-used and re-read on return");
    }

    [Fact]
    public void TouchingATile_MakesItMostRecentlyUsed_SoItSurvivesEviction()
    {
        long twoTiles = 2 * 16L * sizeof(float);
        var loaded = new List<DemTileKey>();
        var cache = new BakedDemTileCache(k => { loaded.Add(k); return TileOfCells(k, 16); }, maxBytes: twoTiles);

        cache.Load(Key(1));
        cache.Load(Key(2));
        cache.Load(Key(1)); // touch Key(1) → now Key(2) is the least-recently-used
        cache.Load(Key(3)); // over budget → evicts Key(2), NOT Key(1)

        loaded.Clear();
        cache.Load(Key(1)).Should().NotBeNull();
        loaded.Should().BeEmpty("Key(1) was touched, so it survived and is still cached");

        cache.Load(Key(2));
        loaded.Should().ContainSingle().Which.Should().Be(Key(2), "Key(2) was the evicted one and re-reads from disk");
    }

    [Fact]
    public void ResidentBytes_TracksTheSumOfCachedTileGeometry()
    {
        var cache = new BakedDemTileCache(k => TileOfCells(k, 16), maxBytes: 1L << 30);

        cache.Load(Key(1));
        cache.Load(Key(2));

        cache.ResidentBytes.Should().Be(2 * 16L * sizeof(float));
    }

    [Fact]
    public void ConcurrentLoads_AreThreadSafe_AndReadEachKeyFromDiskAtMostOnce()
    {
        // The streaming manager loads tiles from a Parallel.For, so the cache is hit from many threads at once.
        // With a generous budget (no eviction), each distinct key must be disk-read at most once across the race.
        var diskReads = new ConcurrentDictionary<DemTileKey, int>();
        var cache = new BakedDemTileCache(
            k => { diskReads.AddOrUpdate(k, 1, (_, n) => n + 1); return TileOfCells(k, 16); },
            maxBytes: 1L << 30);

        var keys = Enumerable.Range(0, 64).Select(Key).ToArray();
        Parallel.For(0, 8 * keys.Length, i => cache.Load(keys[i % keys.Length]));

        cache.Count.Should().Be(keys.Length);
        diskReads.Values.Should().OnlyContain(n => n == 1, "no key is read from disk more than once");
    }
}