using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour pinning for <see cref="DemRasterDownsampler.SubsampleToMaxCells"/>. Dense LiDAR rasters
/// (z16 ≈ 1.5 m/px) carry millions of samples — fine for the renderer, but ruinous for work that scans
/// a ground-relative window per cell (peak detection's 550 m dominance window becomes ~366 cells wide,
/// turning into ~10¹² ops). This decimates the raster to a sample budget so that work stays cheap,
/// independently of the larger budget the renderer handles.
/// </summary>
public sealed class DemRasterDownsamplerTests
{
    private static DemRaster Raster(int cols, int rows)
    {
        var bounds = new MapBounds(new GeoPoint(49.0, 19.0), new GeoPoint(50.0, 20.0));
        var samples = new float[cols * rows];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = i;
        }

        return new DemRaster(cols, rows, bounds, samples, -9999f);
    }

    [Fact]
    public void SubsampleToMaxCells_ReturnsSameInstance_WhenAlreadyWithinBudget()
    {
        var raster = Raster(10, 10);

        DemRaster result = DemRasterDownsampler.SubsampleToMaxCells(raster, maxCells: 1000);

        result.Should().BeSameAs(raster, "100 cells already fit the 1000 budget — no copy needed");
    }

    [Fact]
    public void SubsampleToMaxCells_BringsCellCountWithinBudget()
    {
        var raster = Raster(100, 100);

        DemRaster result = DemRasterDownsampler.SubsampleToMaxCells(raster, maxCells: 500);

        ((long)result.Columns * result.Rows).Should().BeLessThanOrEqualTo(500);
    }

    [Fact]
    public void SubsampleToMaxCells_PreservesBoundsAndNoDataSentinel()
    {
        var raster = Raster(100, 100);

        DemRaster result = DemRasterDownsampler.SubsampleToMaxCells(raster, maxCells: 400);

        result.West.Should().Be(raster.West);
        result.North.Should().Be(raster.North);
        result.NoDataValue.Should().Be(raster.NoDataValue);
    }

    [Fact]
    public void SubsampleToMaxCells_RejectsBudgetSmallerThanTheMinimumRaster()
    {
        var raster = Raster(10, 10);

        var act = () => DemRasterDownsampler.SubsampleToMaxCells(raster, maxCells: 3);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}