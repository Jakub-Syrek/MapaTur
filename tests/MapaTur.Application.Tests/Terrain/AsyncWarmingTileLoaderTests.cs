using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// <see cref="AsyncWarmingTileLoader"/> — the non-blocking front the FRAME-THREAD tile consumers (camera
/// floor, walk ground, fireball contact probes) use instead of the blocking loaders. Contract: a cold key
/// returns null IMMEDIATELY (the caller falls back to a coarser level for a frame) while the underlying
/// load runs in the background exactly once; once warm, the value is served through the underlying cache.
/// The 2026-07-11 stutter class this kills: frame gaps of 170–320 ms with IDLE CPU and GPU — the frame
/// thread parked on a synchronous .bdt disk read (or a 65k-sample synthesis) inside the elevation sampler.
/// </summary>
public sealed class AsyncWarmingTileLoaderTests
{
    private static readonly DemTileKey Key = new(17, 72821, 44890);

    private static BakedDemTile Tile(DemTileKey key)
    {
        (double west, double south, double east, double north) = SlippyTileMath.TileBounds(key.X, key.Y, key.Zoom);
        var bounds = new MapBounds(new GeoPoint(south, west), new GeoPoint(north, east));
        var heights = new float[4 * 4];
        Array.Fill(heights, 1500f);
        return new BakedDemTile(key.Zoom, key.X, key.Y, 4, 4, bounds, -9999.0, heights);
    }

    [Fact]
    public async Task TryGetOrWarm_ColdKey_ReturnsNullImmediately_ThenServesTheWarmedTile()
    {
        var gate = new TaskCompletionSource();
        int loads = 0;
        var loader = new AsyncWarmingTileLoader(key =>
        {
            Interlocked.Increment(ref loads);
            gate.Task.Wait();
            return Tile(key);
        });

        loader.TryGetOrWarm(Key).Should().BeNull("a cold key must not block the calling (frame) thread");
        gate.SetResult();
        await loader.DrainForTestsAsync();
        loads.Should().Be(1, "exactly one background warm ran");

        loader.TryGetOrWarm(Key).Should().NotBeNull("the background warm has completed");
        loads.Should().Be(2, "a warm hit re-reads THROUGH the underlying cache (a RAM hit in production)");
    }

    [Fact]
    public async Task TryGetOrWarm_ManyProbesWhileCold_LoadUnderlyingOnlyOnce()
    {
        var gate = new TaskCompletionSource();
        int loads = 0;
        var loader = new AsyncWarmingTileLoader(key =>
        {
            Interlocked.Increment(ref loads);
            gate.Task.Wait();
            return Tile(key);
        });

        for (int i = 0; i < 50; i++)
        {
            loader.TryGetOrWarm(Key).Should().BeNull();
        }

        gate.SetResult();
        await loader.DrainForTestsAsync();
        loads.Should().Be(1, "per-frame probing must not stack duplicate background loads");
    }

    [Fact]
    public async Task TryGetOrWarm_UnderlyingReturnsNull_IsRememberedWithoutRewarmLoops()
    {
        int loads = 0;
        var loader = new AsyncWarmingTileLoader(_ =>
        {
            Interlocked.Increment(ref loads);
            return null;
        });

        loader.TryGetOrWarm(Key).Should().BeNull();
        await loader.DrainForTestsAsync();
        loader.TryGetOrWarm(Key).Should().BeNull("an absent tile stays absent");
        await loader.DrainForTestsAsync();

        loads.Should().Be(1, "a known-absent key must not re-warm every frame");
    }
}