using FluentAssertions;

using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Domain.Tests.Terrain;

public sealed class DemRasterSubsampleTests
{
    private static readonly MapBounds TestBounds = new(
        new GeoPoint(49.10, 19.50),
        new GeoPoint(49.40, 20.40));

    private static DemRaster RampRaster(int cols, int rows)
    {
        // Diagonal ramp so subsampled cells stay distinguishable: elevation = col + row * 10.
        var samples = new float[cols * rows];
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                samples[(r * cols) + c] = c + (r * 10);
            }
        }
        return new DemRaster(cols, rows, TestBounds, samples);
    }

    [Fact]
    public void Subsample_StepOne_ReturnsSameInstance()
    {
        var raster = RampRaster(10, 8);

        DemRaster result = raster.Subsample(1);

        result.Should().BeSameAs(raster);
    }

    [Fact]
    public void Subsample_StepThree_ShrinksDimensions_ByThree()
    {
        // 10×8 stepped by 3 produces ceil(10/3)=4 columns, ceil(8/3)=3 rows.
        var raster = RampRaster(10, 8);

        DemRaster result = raster.Subsample(3);

        result.Columns.Should().Be(4);
        result.Rows.Should().Be(3);
    }

    [Fact]
    public void Subsample_StepTwo_PreservesBounds()
    {
        var raster = RampRaster(10, 8);

        DemRaster result = raster.Subsample(2);

        result.Bounds.Should().Be(TestBounds);
    }

    [Fact]
    public void Subsample_StepTwo_KeepsEverySecondSample()
    {
        // Source row 0: 0, 1, 2, 3, 4, 5, 6, 7, 8, 9; step 2 keeps 0, 2, 4, 6, 8.
        var raster = RampRaster(10, 8);

        DemRaster result = raster.Subsample(2);

        result[0, 0].Should().Be(0f);
        result[1, 0].Should().Be(2f);
        result[2, 0].Should().Be(4f);
        result[0, 1].Should().Be(20f);  // source row 2, col 0
    }

    [Fact]
    public void Subsample_PreservesNoDataSentinel()
    {
        var raster = new DemRaster(4, 4, TestBounds, new float[16], noDataValue: -1234.5f);

        DemRaster result = raster.Subsample(2);

        result.NoDataValue.Should().Be(-1234.5f);
    }

    [Fact]
    public void Subsample_StepZero_Throws()
    {
        var raster = RampRaster(10, 8);

        Action act = () => raster.Subsample(0);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("step");
    }

    [Fact]
    public void Subsample_NegativeStep_Throws()
    {
        var raster = RampRaster(10, 8);

        Action act = () => raster.Subsample(-1);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("step");
    }

    [Fact]
    public void Subsample_StepLargerThanGrid_ReturnsAtLeast2x2()
    {
        // A 10×8 raster subsampled by 50 would shrink to 1×1, which DemRaster rejects (cols/rows ≥ 2).
        // The contract: clamp the effective step so the result is always a valid raster.
        var raster = RampRaster(10, 8);

        DemRaster result = raster.Subsample(50);

        result.Columns.Should().BeGreaterThanOrEqualTo(2);
        result.Rows.Should().BeGreaterThanOrEqualTo(2);
    }
}