using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour pinning for <see cref="TerrainLodBands"/> — distance-based LOD selection (rule #2 of the LOD
/// architecture): a tile near the camera streams at full 1 m, mid-range at a coarser tier, far away it just
/// uses the always-on 30 m base. Pure thresholds; the streamer feeds it the ground distance and uses the
/// returned zoom to pick which tile pyramid level to fetch.
/// </summary>
public sealed class TerrainLodBandsTests
{
    [Fact]
    public void ZoomForDistance_NearCamera_PicksTheFullDetailTier()
    {
        TerrainLodBands.ZoomForDistance(200).Should().Be(TerrainLodBands.NearZoom);
    }

    [Fact]
    public void ZoomForDistance_MidRange_PicksTheMidTier()
    {
        TerrainLodBands.ZoomForDistance(900).Should().Be(TerrainLodBands.MidZoom);
    }

    [Fact]
    public void ZoomForDistance_FarAway_FallsBackToTheBaseTier()
    {
        TerrainLodBands.ZoomForDistance(5000).Should().Be(TerrainLodBands.BaseZoom);
    }

    [Fact]
    public void ZoomForDistance_IsMonotonicallyCoarserWithDistance()
    {
        TerrainLodBands.ZoomForDistance(200).Should().BeGreaterThanOrEqualTo(TerrainLodBands.ZoomForDistance(900));
        TerrainLodBands.ZoomForDistance(900).Should().BeGreaterThanOrEqualTo(TerrainLodBands.ZoomForDistance(5000));
    }

    [Fact]
    public void ZoomForDistance_AtTheNearBoundary_IsStillFullDetail()
    {
        // The near band is inclusive of its upper edge so a tile exactly at the threshold isn't downgraded.
        TerrainLodBands.ZoomForDistance(TerrainLodBands.NearMaxMeters).Should().Be(TerrainLodBands.NearZoom);
    }

    [Fact]
    public void ZoomForDistance_NegativeDistance_Throws()
    {
        var act = () => TerrainLodBands.ZoomForDistance(-1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}