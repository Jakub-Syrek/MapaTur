using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour pinning for <see cref="DemRasterCoverage.HasTerrain"/> — the NoData-fallback gate (rule #12 of
/// the LOD architecture). GUGiK returns empty/zero tiles past the Polish border; a streamed 1 m detail tile
/// that's effectively flat at zero must NOT replace the coarse base (it would show a blank plateau), so the
/// streamer asks this before overlaying. Real Tatra terrain sits well above the floor; an empty tile is ~0.
/// </summary>
public sealed class DemRasterCoverageTests
{
    private static DemRaster Raster(params float[] samples) =>
        new(2, 2, new MapBounds(new GeoPoint(49.1, 19.9), new GeoPoint(49.2, 20.0)), samples, -9999f);

    [Fact]
    public void HasTerrain_RealMountainElevations_IsTrue()
    {
        DemRasterCoverage.HasTerrain(Raster(1600f, 1700f, 1800f, 1883f), minTopMeters: 100).Should().BeTrue();
    }

    [Fact]
    public void HasTerrain_AllZeros_IsFalse()
    {
        DemRasterCoverage.HasTerrain(Raster(0f, 0f, 0f, 0f), minTopMeters: 100).Should().BeFalse();
    }

    [Fact]
    public void HasTerrain_TopBelowFloor_IsFalse()
    {
        DemRasterCoverage.HasTerrain(Raster(10f, 20f, 30f, 40f), minTopMeters: 100).Should().BeFalse();
    }

    [Fact]
    public void HasTerrain_TopAtFloor_IsTrue()
    {
        DemRasterCoverage.HasTerrain(Raster(50f, 80f, 100f, 90f), minTopMeters: 100).Should().BeTrue();
    }

    [Fact]
    public void HasTerrain_NullRaster_Throws()
    {
        var act = () => DemRasterCoverage.HasTerrain(null!, minTopMeters: 100);

        act.Should().Throw<ArgumentNullException>();
    }
}