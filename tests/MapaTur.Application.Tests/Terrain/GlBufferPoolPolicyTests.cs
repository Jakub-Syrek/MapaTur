using MapaTur.Application.Terrain;

using Xunit;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// P0 wyciek pamięci (2026-08-06): polityka poolingu jednostek buforów GL dla mesh kafli terenu.
/// Zmierzono (08-02, dev/p0-morning): Gen/Delete tysięcy różnorozmiarowych VBO w locie zostawia osad
/// w pulach ANGLE/D3D11 (+1 GB ws/lot przy STAŁYCH licznikach GlTrack). Naprawa = jednostki o
/// pojemnościach z drabinki klas, alokowane raz i wypełniane BufferSubData. Ta klasa podejmuje
/// wyłącznie DECYZJE (czysta logika, zero GL) — reuse / create / evict — i jest testowana tutaj.
/// </summary>
public class GlBufferPoolPolicyTests
{
    // 40 B/wierzchołek = pos 12 + color 4 + normal 12 + uv 8 + detail 4 (sekstet kafla terenu).
    private static GlBufferPoolPolicy<object> NewPolicy(long maxFreeBytes = long.MaxValue)
        => new(bytesPerVertex: 40, bytesPerIndex: 4, maxFreeBytes);

    [Fact]
    public void should_round_up_to_at_least_requested_count()
    {
        for (int n = 1; n <= 70_000; n = (n * 3 / 2) + 1)
        {
            Assert.True(GlBufferPoolPolicy<object>.RoundUpCap(n) >= n, $"cap must cover request n={n}");
        }
    }

    [Fact]
    public void should_be_idempotent_on_ladder_values()
    {
        int cap = GlBufferPoolPolicy<object>.RoundUpCap(66_556);

        Assert.Equal(cap, GlBufferPoolPolicy<object>.RoundUpCap(cap));
    }

    [Fact]
    public void should_keep_ladder_waste_at_most_a_quarter()
    {
        // Klasy co ≤×1,25: marnotrawstwo VRAM ograniczone, a rozmiary POWTARZALNE dla sterownika.
        for (int n = 64; n <= 400_000; n = (n * 7 / 5) + 3)
        {
            int cap = GlBufferPoolPolicy<object>.RoundUpCap(n);
            Assert.True(cap <= (long)n * 5 / 4 + 1, $"cap {cap} wastes more than 25% over n={n}");
        }
    }

    [Fact]
    public void should_create_new_unit_when_pool_empty()
    {
        GlBufferPoolPolicy<object> pool = NewPolicy();

        GlPoolAcquire<object> got = pool.Acquire(vertexCount: 1000, indexCount: 5000);

        Assert.Null(got.Reused);
        Assert.True(got.VertexCap >= 1000);
        Assert.True(got.IndexCap >= 5000);
        Assert.Equal(GlBufferPoolPolicy<object>.RoundUpCap(1000), got.VertexCap);
        Assert.Equal(GlBufferPoolPolicy<object>.RoundUpCap(5000), got.IndexCap);
    }

    [Fact]
    public void should_reuse_released_unit_for_same_size_class()
    {
        GlBufferPoolPolicy<object> pool = NewPolicy();
        GlPoolAcquire<object> first = pool.Acquire(1000, 5000);
        var unit = new object();
        pool.Release(unit, first.VertexCap, first.IndexCap);

        // Inna liczba wierzchołków, ale ta sama klasa rozmiaru → ta sama jednostka wraca.
        GlPoolAcquire<object> second = pool.Acquire(vertexCount: first.VertexCap - 3, indexCount: first.IndexCap - 7);

        Assert.Same(unit, second.Reused);
        Assert.Equal(first.VertexCap, second.VertexCap);
        Assert.Equal(first.IndexCap, second.IndexCap);
    }

    [Fact]
    public void should_not_reuse_unit_from_a_different_size_class()
    {
        GlBufferPoolPolicy<object> pool = NewPolicy();
        GlPoolAcquire<object> small = pool.Acquire(100, 500);
        pool.Release(new object(), small.VertexCap, small.IndexCap);

        GlPoolAcquire<object> big = pool.Acquire(60_000, 380_000);

        Assert.Null(big.Reused); // mniejsza jednostka nie pomieści — tworzymy nową, nie zgadujemy
    }

    [Fact]
    public void should_prefer_most_recently_released_unit_in_bucket()
    {
        GlBufferPoolPolicy<object> pool = NewPolicy();
        GlPoolAcquire<object> spec = pool.Acquire(1000, 5000);
        var older = new object();
        var newer = new object();
        pool.Release(older, spec.VertexCap, spec.IndexCap);
        pool.Release(newer, spec.VertexCap, spec.IndexCap);

        GlPoolAcquire<object> got = pool.Acquire(1000, 5000);

        Assert.Same(newer, got.Reused); // LIFO: najcieplejsza jednostka pierwsza
    }

    [Fact]
    public void should_not_hand_out_same_unit_twice()
    {
        GlBufferPoolPolicy<object> pool = NewPolicy();
        GlPoolAcquire<object> spec = pool.Acquire(1000, 5000);
        var unit = new object();
        pool.Release(unit, spec.VertexCap, spec.IndexCap);

        GlPoolAcquire<object> first = pool.Acquire(1000, 5000);
        GlPoolAcquire<object> second = pool.Acquire(1000, 5000);

        Assert.Same(unit, first.Reused);
        Assert.Null(second.Reused);
    }

    [Fact]
    public void should_account_free_bytes_from_unit_capacities()
    {
        GlBufferPoolPolicy<object> pool = NewPolicy();
        GlPoolAcquire<object> spec = pool.Acquire(1000, 5000);

        pool.Release(new object(), spec.VertexCap, spec.IndexCap);

        long expected = (40L * spec.VertexCap) + (4L * spec.IndexCap);
        Assert.Equal(expected, pool.FreeBytes);
        Assert.Equal(1, pool.FreeCount);
    }

    [Fact]
    public void should_evict_oldest_free_units_over_byte_budget()
    {
        // Budżet mieści DWIE jednostki klasy (1000,5000) — trzecia zwolniona wypycha NAJSTARSZĄ.
        GlBufferPoolPolicy<object> probe = NewPolicy();
        GlPoolAcquire<object> spec = probe.Acquire(1000, 5000);
        long unitBytes = (40L * spec.VertexCap) + (4L * spec.IndexCap);
        GlBufferPoolPolicy<object> pool = NewPolicy(maxFreeBytes: (unitBytes * 2) + 1);

        var a = new object();
        var b = new object();
        var c = new object();
        Assert.Empty(pool.Release(a, spec.VertexCap, spec.IndexCap));
        Assert.Empty(pool.Release(b, spec.VertexCap, spec.IndexCap));
        System.Collections.Generic.IReadOnlyList<object> evicted = pool.Release(c, spec.VertexCap, spec.IndexCap);

        Assert.Single(evicted);
        Assert.Same(a, evicted[0]); // LRU: najdłużej bezczynna jednostka do skasowania
        Assert.Equal(2, pool.FreeCount);
        Assert.True(pool.FreeBytes <= (unitBytes * 2) + 1);
    }

    [Fact]
    public void should_forget_everything_on_reset_without_evictions()
    {
        // Utrata kontekstu GL: uchwyty umarły z kontekstem — pula MUSI zapomnieć bez kasowania.
        GlBufferPoolPolicy<object> pool = NewPolicy();
        GlPoolAcquire<object> spec = pool.Acquire(1000, 5000);
        pool.Release(new object(), spec.VertexCap, spec.IndexCap);

        pool.Reset();

        Assert.Equal(0, pool.FreeCount);
        Assert.Equal(0L, pool.FreeBytes);
        Assert.Null(pool.Acquire(1000, 5000).Reused);
    }

    [Fact]
    public void should_drain_all_free_units_for_live_context_teardown()
    {
        GlBufferPoolPolicy<object> pool = NewPolicy();
        GlPoolAcquire<object> spec = pool.Acquire(1000, 5000);
        var a = new object();
        var b = new object();
        pool.Release(a, spec.VertexCap, spec.IndexCap);
        pool.Release(b, spec.VertexCap, spec.IndexCap);

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
        GlBufferPoolPolicy<object> pool = NewPolicy();
        GlPoolAcquire<object> spec = pool.Acquire(1000, 5000); // miss (pusta pula)
        pool.Release(new object(), spec.VertexCap, spec.IndexCap);
        pool.Acquire(1000, 5000);                              // hit
        pool.Acquire(1000, 5000);                              // miss (wydana)

        Assert.Equal(1, pool.Hits);
        Assert.Equal(2, pool.Misses);
    }
}