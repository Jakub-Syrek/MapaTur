using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour pinning for <see cref="DemRegionCachePlan"/>. The offline-first widening (roam the Tatras at
/// 1 m, tiles stay on disk after first load) needs one primitive: given the tiles a region requires and a
/// "is this tile already cached?" probe, split them into what's local versus what still has to be fetched,
/// and report coverage. That drives a resumable "download Tatras offline" (fetch only the missing, show a
/// real %) and an offline-first LOD ("serve from disk, hit the network only for never-seen tiles").
/// </summary>
public sealed class DemRegionCachePlanTests
{
    private static readonly DemTileKey A = new(16, 1, 1);
    private static readonly DemTileKey B = new(16, 2, 1);
    private static readonly DemTileKey C = new(16, 1, 2);
    private static readonly DemTileKey D = new(16, 2, 2);
    private static readonly DemTileKey[] Region = { A, B, C, D };

    [Fact]
    public void For_AllTilesCached_HasNothingMissing_AndFullCoverage()
    {
        var plan = DemRegionCachePlan.For(Region, _ => true);

        plan.Missing.Should().BeEmpty();
        plan.CachedCount.Should().Be(4);
        plan.TotalCount.Should().Be(4);
        plan.IsFullyCached.Should().BeTrue();
        plan.CoverageFraction.Should().Be(1.0);
    }

    [Fact]
    public void For_NoTilesCached_MarksEveryTileMissing_AndZeroCoverage()
    {
        var plan = DemRegionCachePlan.For(Region, _ => false);

        plan.Missing.Should().Equal(A, B, C, D);
        plan.CachedCount.Should().Be(0);
        plan.CoverageFraction.Should().Be(0.0);
        plan.IsFullyCached.Should().BeFalse();
    }

    [Fact]
    public void For_PartiallyCached_PartitionsTilesAndComputesFraction()
    {
        // A and C already on disk; B and D still to fetch.
        var cached = new HashSet<DemTileKey> { A, C };

        var plan = DemRegionCachePlan.For(Region, cached.Contains);

        plan.Missing.Should().Equal(new[] { B, D }); // source order, only the uncached tiles
        plan.CachedCount.Should().Be(2);
        plan.CoverageFraction.Should().Be(0.5);
        plan.IsFullyCached.Should().BeFalse();
    }

    [Fact]
    public void For_EmptyRegion_IsVacuouslyFullyCached()
    {
        var plan = DemRegionCachePlan.For(Array.Empty<DemTileKey>(), _ => false);

        plan.TotalCount.Should().Be(0);
        plan.Missing.Should().BeEmpty();
        plan.IsFullyCached.Should().BeTrue();
        plan.CoverageFraction.Should().Be(1.0, "no tiles to fetch means there is nothing left uncovered");
    }

    [Fact]
    public void For_NullArguments_Throws()
    {
        var nullTiles = () => DemRegionCachePlan.For(null!, _ => true);
        var nullProbe = () => DemRegionCachePlan.For(Region, null!);

        nullTiles.Should().Throw<ArgumentNullException>();
        nullProbe.Should().Throw<ArgumentNullException>();
    }
}