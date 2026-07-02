using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// The roughness cache (Step A) must be a pure SPEED optimization: <see cref="PerTileDetailPlanner.PlanDetailed"/>
/// with the cache produces a BYTE-IDENTICAL plan to without it, and a window that slides over the absolute grid
/// only re-scans roughness for the newly-entered tiles (overlap tiles reuse the cached value). Roughness is a
/// pure function of the crop pixels + the two knobs, so caching it by absolute tile identity cannot change any
/// decision — these tests pin that.
/// </summary>
public sealed class PerTileRoughnessCacheTests
{
    private static readonly int[] Steps = { 1, 2, 4, 8 };
    private static readonly RoughnessLodPreset Preset = RoughnessLodPreset.Balanced;
    private const float VerticalExaggeration = 1f;
    private const double FovY = 1.0;
    private const double ViewportHeight = 800;
    private const double MaxErrorPixels = 2.0;
    private const long HugeBudget = long.MaxValue;
    private const int Quantum = 32;

    // A deterministic, spatially-varying surface so different tiles have genuinely different roughness
    // (a flat surface would read 0 everywhere and hide a cache that returned the wrong tile's value).
    private static float Elevation(int absCol, int absRow)
    {
        double ridge = 120.0 * Math.Sin(absCol * 0.13) * Math.Cos(absRow * 0.11);
        double saw = (absCol + absRow) % 7 == 0 ? 40.0 : 0.0;
        return (float)(1500.0 + ridge + saw);
    }

    // Builds a window whose NW corner sits at absolute cell (absCol0, absRow0) of the shared infinite grid,
    // so two windows that overlap describe the SAME ground over the overlap (identical samples).
    private static DemRaster Window(int absCol0, int absRow0, int side)
    {
        var samples = new float[side * side];
        for (int r = 0; r < side; r++)
        {
            for (int c = 0; c < side; c++)
            {
                samples[(r * side) + c] = Elevation(absCol0 + c, absRow0 + r);
            }
        }

        // The geographic bounds are arbitrary but must be consistent; the planner derives cell pitch from them.
        var sw = new GeoPoint(49.20, 19.98);
        var ne = new GeoPoint(49.21, 19.99);
        return new DemRaster(side, side, new MapBounds(sw, ne), samples);
    }

    private static GeoPoint AnchorFor(DemRaster r) =>
        new((r.North + r.South) / 2.0, (r.West + r.East) / 2.0);

    private static PerTilePlanResult Plan(DemRaster full, int absCol0, int absRow0, PerTileRoughnessCache? cache)
        => PerTileDetailPlanner.PlanDetailed(
            full, new Vector3(0f, 0f, 4000f), AnchorFor(full), VerticalExaggeration,
            gridN: 4, Steps, FovY, ViewportHeight, MaxErrorPixels, Preset, HugeBudget,
            roughnessStride: 1, roughnessNeighborDistance: 1,
            cameraBubbleRadiusMeters: 0.0, cameraBubbleStep: 1,
            absColOrigin: absCol0, absRowOrigin: absRow0, tileQuantum: Quantum, roughnessCache: cache);

    [Fact]
    public void PlanDetailed_WithCache_ProducesIdenticalDecisionsToWithout()
    {
        DemRaster full = Window(absCol0: 1024, absRow0: 2048, side: 96);

        IReadOnlyList<PerTileLodDecision> noCache = Plan(full, 1024, 2048, cache: null).Tiles;

        var cache = new PerTileRoughnessCache();
        cache.BeginRound();
        IReadOnlyList<PerTileLodDecision> withCache = Plan(full, 1024, 2048, cache).Tiles;

        withCache.Should().Equal(noCache, "the cache only avoids recomputation; every per-tile decision is identical");
    }

    [Fact]
    public void PlanDetailed_WithWarmCache_StillProducesIdenticalDecisions()
    {
        // Run twice through the SAME cache (second call is all hits) — the cached values must reproduce the plan.
        DemRaster full = Window(absCol0: 1024, absRow0: 2048, side: 96);
        IReadOnlyList<PerTileLodDecision> reference = Plan(full, 1024, 2048, cache: null).Tiles;

        var cache = new PerTileRoughnessCache();
        cache.BeginRound();
        _ = Plan(full, 1024, 2048, cache).Tiles; // warm
        cache.BeginRound();
        IReadOnlyList<PerTileLodDecision> warm = Plan(full, 1024, 2048, cache).Tiles;

        cache.Hits.Should().BeGreaterThan(0, "the second pass over the same window must hit the cache");
        cache.Misses.Should().Be(0, "no tile changed, so the warm pass recomputes nothing");
        warm.Should().Equal(reference, "cached roughness reproduces the exact same plan");
    }

    [Fact]
    public void PlanDetailed_ShiftedWindow_OnlyRecomputesRoughnessForNewlyEnteredTiles()
    {
        // First window, then a window shifted by exactly one quantum east+south: the overlapping interior tiles
        // sit at the SAME absolute origins and SAME size, so they must be cache HITS; only the freshly-entered
        // strip recomputes. Proves a re-center scans only the new ground, not the whole window.
        const int side = 96; // 3 quanta → a 3×3 interior grid of full quanta plus edges
        DemRaster first = Window(absCol0: 0, absRow0: 0, side: side);
        DemRaster shifted = Window(absCol0: Quantum, absRow0: Quantum, side: side);

        var cache = new PerTileRoughnessCache();
        cache.BeginRound();
        _ = Plan(first, 0, 0, cache).Tiles;
        int firstMisses = cache.Misses;
        firstMisses.Should().BeGreaterThan(0, "the first window computes every tile fresh");

        cache.BeginRound();
        _ = Plan(shifted, Quantum, Quantum, cache).Tiles;

        cache.Hits.Should().BeGreaterThan(0, "the overlapping tiles share absolute identity and must be reused");
        cache.Misses.Should().BeLessThan(firstMisses,
            "a one-quantum shift re-scans only the newly-entered tiles, not the whole window");
    }

    [Fact]
    public void EvictUnused_DropsTilesNotTouchedThisRound()
    {
        DemRaster first = Window(absCol0: 0, absRow0: 0, side: 96);
        DemRaster faraway = Window(absCol0: 10_000, absRow0: 10_000, side: 96);

        var cache = new PerTileRoughnessCache();
        cache.BeginRound();
        _ = Plan(first, 0, 0, cache).Tiles;
        int afterFirst = cache.Count;
        afterFirst.Should().BeGreaterThan(0);

        // A completely disjoint window touches none of the first window's tiles; the second round computes its
        // OWN tiles fresh (all misses, no hits), and after eviction the first window's tiles are gone — so the
        // cache cannot grow without bound as the camera roams. Count then equals exactly this round's misses.
        cache.BeginRound();
        _ = Plan(faraway, 10_000, 10_000, cache).Tiles;
        cache.Hits.Should().Be(0, "a disjoint window shares no absolute tile identity with the first");
        int secondRoundTiles = cache.Misses;
        cache.EvictUnused();

        cache.Count.Should().Be(secondRoundTiles,
            "eviction drops every untouched first-window tile, leaving only the current window's tiles");
    }
}