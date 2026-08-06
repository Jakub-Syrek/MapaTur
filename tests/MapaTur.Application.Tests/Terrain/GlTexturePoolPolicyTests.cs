using MapaTur.Application.Terrain;

using Xunit;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// P0 (2026-08-06): pula tekstur staging bazy orto. Tier odległości flipuje celę near↔far — dziś każdy flip
/// to DeleteTexture + mutable TexImage2D w nowym rozmiarze (wzorzec, który na maskach zostawiał ~1 GB/lot
/// osadu ANGLE — patrz EnsureImmutableMaskStorage). Naprawa: immutable TexStorage2D + reuse po DOKŁADNYM
/// (W,H) — flip tej samej celi trafia w pulę. Tu testujemy czystą politykę decyzji (zero GL).
/// </summary>
public class GlTexturePoolPolicyTests
{
    private static GlTexturePoolPolicy<object> NewPool(long maxFreeBytes = long.MaxValue)
        => new(bytesPerPixel: 4, maxFreeBytes);

    [Fact]
    public void should_create_when_pool_empty()
    {
        GlTexturePoolPolicy<object> pool = NewPool();

        Assert.Null(pool.Acquire(4096, 2048));
    }

    [Fact]
    public void should_reuse_exact_size_most_recent_first()
    {
        GlTexturePoolPolicy<object> pool = NewPool();
        var older = new object();
        var newer = new object();
        pool.Release(older, 4096, 2048);
        pool.Release(newer, 4096, 2048);

        Assert.Same(newer, pool.Acquire(4096, 2048));
        Assert.Same(older, pool.Acquire(4096, 2048));
        Assert.Null(pool.Acquire(4096, 2048));
    }

    [Fact]
    public void should_not_reuse_different_size()
    {
        GlTexturePoolPolicy<object> pool = NewPool();
        pool.Release(new object(), 4096, 2048);

        Assert.Null(pool.Acquire(2048, 4096)); // wymiary zamienione ≠ ten sam kształt storage
    }

    [Fact]
    public void should_account_free_bytes_with_mip_overhead()
    {
        GlTexturePoolPolicy<object> pool = NewPool();

        pool.Release(new object(), 1024, 512);

        // RGBA8 + pełny łańcuch mipów ≈ ×4/3 — księgowość taka sama jak [Mem] w rendererze.
        Assert.Equal(1024L * 512 * 4 * 4 / 3, pool.FreeBytes);
        Assert.Equal(1, pool.FreeCount);
    }

    [Fact]
    public void should_evict_oldest_over_byte_budget()
    {
        long one = GlTexturePoolPolicy<object>.EstimateBytes(1024, 1024, 4);
        GlTexturePoolPolicy<object> pool = new(bytesPerPixel: 4, maxFreeBytes: (one * 2) + 1);
        var a = new object();
        var b = new object();
        var c = new object();

        Assert.Empty(pool.Release(a, 1024, 1024));
        Assert.Empty(pool.Release(b, 1024, 1024));
        System.Collections.Generic.IReadOnlyList<object> evicted = pool.Release(c, 1024, 1024);

        Assert.Single(evicted);
        Assert.Same(a, evicted[0]);
        Assert.Equal(2, pool.FreeCount);
    }

    [Fact]
    public void should_reset_without_evictions_on_context_loss()
    {
        GlTexturePoolPolicy<object> pool = NewPool();
        pool.Release(new object(), 4096, 4096);

        pool.Reset();

        Assert.Equal(0, pool.FreeCount);
        Assert.Equal(0L, pool.FreeBytes);
        Assert.Null(pool.Acquire(4096, 4096));
    }

    [Fact]
    public void should_drain_all_free_textures_for_live_context_teardown()
    {
        GlTexturePoolPolicy<object> pool = NewPool();
        var a = new object();
        var b = new object();
        pool.Release(a, 1024, 512);
        pool.Release(b, 2048, 2048);

        var drained = new System.Collections.Generic.List<object>();
        pool.DrainTo(drained);

        Assert.Equal(2, drained.Count);
        Assert.Contains(a, drained);
        Assert.Contains(b, drained);
        Assert.Equal(0, pool.FreeCount);
        Assert.Equal(0L, pool.FreeBytes);
    }

    [Fact]
    public void should_count_hits_and_misses()
    {
        GlTexturePoolPolicy<object> pool = NewPool();
        Assert.Null(pool.Acquire(512, 512));      // miss
        pool.Release(new object(), 512, 512);
        Assert.NotNull(pool.Acquire(512, 512));   // hit
        Assert.Null(pool.Acquire(512, 512));      // miss

        Assert.Equal(1, pool.Hits);
        Assert.Equal(2, pool.Misses);
    }
}