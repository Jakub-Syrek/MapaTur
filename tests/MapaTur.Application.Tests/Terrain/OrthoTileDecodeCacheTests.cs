using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour of <see cref="OrthoTileDecodeCache"/>: a bounded, thread-safe LRU that sits in FRONT of the
/// SkiaSharp WebP decode so a 512-px ortho detail tile shared by two neighbouring detail cells (they overlap by
/// a 128 m margin = ≥1 tile) — or re-visited when the camera pans back — is decoded ONCE and then served from
/// RAM. The decode is injected, so every test asserts on how many times the SOURCE (disk decode) was consulted.
/// Mirrors <see cref="BakedDemTileCache"/> (the proven RAM-cache doctrine), keyed by the global tile (i,j).
/// </summary>
public sealed class OrthoTileDecodeCacheTests
{
    // A decoded tile of a known small size so budgets can be expressed in whole-tile units.
    private const int TileBytes = 64;

    private static byte[] Tile(int i, int j)
    {
        var buf = new byte[TileBytes];
        buf[0] = (byte)i;
        buf[1] = (byte)j;
        return buf;
    }

    [Fact]
    public void Miss_DecodesFromSource_AndReturnsTheTile()
    {
        var cache = new OrthoTileDecodeCache((i, j) => Tile(i, j), maxBytes: 1L << 30);

        byte[]? tile = cache.Get(3, 7);

        tile.Should().NotBeNull();
        tile![0].Should().Be(3);
        tile[1].Should().Be(7);
        cache.Misses.Should().Be(1);
        cache.Hits.Should().Be(0);
        cache.Count.Should().Be(1);
    }

    [Fact]
    public void Hit_SecondGetOfSameTile_DoesNotDecodeAgain()
    {
        int decodes = 0;
        var cache = new OrthoTileDecodeCache(
            (i, j) => { decodes++; return Tile(i, j); }, maxBytes: 1L << 30);

        byte[]? first = cache.Get(3, 7);
        byte[]? second = cache.Get(3, 7);

        decodes.Should().Be(1, "the second get is served from RAM, not a re-decode");
        second.Should().BeSameAs(first, "the cache hands back the identical cached array");
        cache.Hits.Should().Be(1);
        cache.Misses.Should().Be(1);
    }

    [Fact]
    public void DifferentTiles_AreCachedIndependently()
    {
        var cache = new OrthoTileDecodeCache((i, j) => Tile(i, j), maxBytes: 1L << 30);

        cache.Get(1, 1);
        cache.Get(1, 2);
        cache.Get(2, 1);

        cache.Count.Should().Be(3);
        cache.Misses.Should().Be(3);
    }

    [Fact]
    public void NullFromSource_IsNotCached_SoTheTileCanBeRetried()
    {
        // A missing tile (fully-nodata → never written to disk, in _nodata_skip.txt) decodes to null; caching the
        // absence would make it a permanent hole even after the fetcher later fills it.
        int decodes = 0;
        byte[]? Source(int i, int j)
        {
            decodes++;
            return decodes == 1 ? null : Tile(i, j); // fails once, then succeeds
        }

        var cache = new OrthoTileDecodeCache(Source, maxBytes: 1L << 30);

        cache.Get(5, 5).Should().BeNull();
        cache.Count.Should().Be(0, "a null result is not stored");
        cache.Get(5, 5).Should().NotBeNull("the retry decodes again and now succeeds");
        decodes.Should().Be(2);
        cache.Count.Should().Be(1);
    }

    [Fact]
    public void OverByteBudget_EvictsLeastRecentlyUsedFirst()
    {
        long twoTiles = 2 * TileBytes;
        int decodesForOne = 0;
        var cache = new OrthoTileDecodeCache(
            (i, j) => { if ((i, j) == (1, 0)) { decodesForOne++; } return Tile(i, j); }, maxBytes: twoTiles);

        cache.Get(1, 0);
        cache.Get(2, 0);
        cache.Get(3, 0); // over budget → evicts the oldest, (1,0)

        cache.Count.Should().Be(2);
        cache.ResidentBytes.Should().BeLessThanOrEqualTo(twoTiles);

        cache.Get(1, 0); // was evicted → must decode again
        decodesForOne.Should().Be(2, "(1,0) was evicted as least-recently-used and re-decoded on return");
    }

    [Fact]
    public void TouchingATile_MakesItMostRecentlyUsed_SoItSurvivesEviction()
    {
        long twoTiles = 2 * TileBytes;
        var decoded = new List<(int, int)>();
        var cache = new OrthoTileDecodeCache(
            (i, j) => { decoded.Add((i, j)); return Tile(i, j); }, maxBytes: twoTiles);

        cache.Get(1, 0);
        cache.Get(2, 0);
        cache.Get(1, 0); // touch (1,0) → now (2,0) is the least-recently-used
        cache.Get(3, 0); // over budget → evicts (2,0), NOT (1,0)

        decoded.Clear();
        cache.Get(1, 0).Should().NotBeNull();
        decoded.Should().BeEmpty("(1,0) was touched, so it survived and is still cached");

        cache.Get(2, 0);
        decoded.Should().ContainSingle().Which.Should().Be((2, 0), "(2,0) was evicted and re-decodes");
    }

    [Fact]
    public void ResidentBytes_TracksTheSumOfCachedTileSizes()
    {
        var cache = new OrthoTileDecodeCache((i, j) => Tile(i, j), maxBytes: 1L << 30);

        cache.Get(1, 0);
        cache.Get(2, 0);

        cache.ResidentBytes.Should().Be(2 * TileBytes);
    }

    [Fact]
    public void ConcurrentGets_AreThreadSafe_AndDecodeEachTileAtMostOnce()
    {
        // The composer assembles a cell from a Parallel-friendly provider and neighbouring cells share edge tiles,
        // so the cache is hit from many threads at once. With a generous budget, each distinct tile decodes once.
        var decodes = new ConcurrentDictionary<(int, int), int>();
        var cache = new OrthoTileDecodeCache(
            (i, j) => { decodes.AddOrUpdate((i, j), 1, (_, n) => n + 1); return Tile(i, j); },
            maxBytes: 1L << 30);

        var keys = Enumerable.Range(0, 64).Select(k => (k, 0)).ToArray();
        Parallel.For(0, 8 * keys.Length, k => cache.Get(keys[k % keys.Length].Item1, 0));

        cache.Count.Should().Be(keys.Length);
        decodes.Values.Should().OnlyContain(n => n == 1, "no tile is decoded more than once");
    }
}
