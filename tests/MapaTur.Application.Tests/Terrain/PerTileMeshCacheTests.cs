using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class PerTileMeshCacheTests
{
    private static DemRaster Raster(int n)
    {
        var bounds = new MapBounds(new GeoPoint(49.0, 20.0), new GeoPoint(49.1, 20.1));
        var samples = new float[n * n];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = 1000f + (i % 97); // varied but bounded terrain
        }

        return new DemRaster(n, n, bounds, samples);
    }

    [Fact]
    public void BuildAdaptiveTiles_SameWindowTwiceWithSharedCache_ReusesEveryBlockInstance()
    {
        DemRaster raster = Raster(16);
        var plan = new[] { new PerTileLodDecision(0, 0, 15, 15, 1) };
        var cache = new PerTileMeshCache();

        cache.BeginRound();
        IReadOnlyList<TerrainMesh3D> first = TerrainMesh3D.BuildAdaptiveTiles(raster, plan, cache: cache);
        cache.EvictUnused();

        cache.BeginRound();
        IReadOnlyList<TerrainMesh3D> second = TerrainMesh3D.BuildAdaptiveTiles(raster, plan, cache: cache);
        cache.EvictUnused();

        second.Should().HaveCount(first.Count);
        first.Should().NotBeEmpty();
        for (int i = 0; i < first.Count; i++)
        {
            second[i].Should().BeSameAs(first[i], "an unchanged block must be reused, not rebuilt");
        }
    }

    [Fact]
    public void BuildAdaptiveTiles_NoCache_BuildsFreshInstancesEachTime()
    {
        DemRaster raster = Raster(16);
        var plan = new[] { new PerTileLodDecision(0, 0, 15, 15, 1) };

        IReadOnlyList<TerrainMesh3D> first = TerrainMesh3D.BuildAdaptiveTiles(raster, plan);
        IReadOnlyList<TerrainMesh3D> second = TerrainMesh3D.BuildAdaptiveTiles(raster, plan);

        second[0].Should().NotBeSameAs(first[0]); // legacy behaviour unchanged: no cache → fresh objects
    }

    [Fact]
    public void EvictUnused_AfterShrinkingTheWindow_DropsBlocksNoLongerBuilt()
    {
        DemRaster raster = Raster(300); // > maxTileSide (250) ⇒ splits into multiple blocks
        var cache = new PerTileMeshCache();

        cache.BeginRound();
        TerrainMesh3D.BuildAdaptiveTiles(raster, new[] { new PerTileLodDecision(0, 0, 299, 299, 1) }, cache: cache);
        cache.EvictUnused();
        int full = cache.Count;
        full.Should().BeGreaterThan(1);

        cache.BeginRound();
        TerrainMesh3D.BuildAdaptiveTiles(raster, new[] { new PerTileLodDecision(0, 0, 120, 120, 1) }, cache: cache);
        cache.EvictUnused();

        cache.Count.Should().BeLessThan(full, "blocks outside the new window must be evicted to bound memory");
    }
}