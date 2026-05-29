using FluentAssertions;

using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Domain.Tests.Terrain;

public sealed class DemRasterTests
{
    private static MapBounds TestBounds() => new(
        new GeoPoint(49.0, 19.0),
        new GeoPoint(50.0, 20.0));

    [Fact]
    public void Constructor_AcceptsValidGrid()
    {
        var samples = new float[2 * 2];

        var raster = new DemRaster(2, 2, TestBounds(), samples);

        raster.Columns.Should().Be(2);
        raster.Rows.Should().Be(2);
        raster.Samples.Should().BeSameAs(samples);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    [InlineData(0, 2)]
    public void Constructor_RejectsTooFewRowsOrColumns(int columns, int rows)
    {
        var act = () => new DemRaster(columns, rows, TestBounds(), new float[columns * rows]);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_RejectsSampleLengthMismatch()
    {
        var act = () => new DemRaster(3, 3, TestBounds(), new float[8]);

        act.Should().Throw<ArgumentException>()
            .WithParameterName("samples");
    }

    [Fact]
    public void Indexer_ReturnsRowMajorSample()
    {
        // 2x3 grid:
        //   row 0 (north): 10, 20, 30
        //   row 1 (south): 40, 50, 60
        var samples = new float[] { 10f, 20f, 30f, 40f, 50f, 60f };
        var raster = new DemRaster(3, 2, TestBounds(), samples);

        raster[0, 0].Should().Be(10f);
        raster[2, 0].Should().Be(30f);
        raster[0, 1].Should().Be(40f);
        raster[2, 1].Should().Be(60f);
    }

    [Fact]
    public void SampleBilinear_AtNorthwestCornerReturnsTopLeftSample()
    {
        var samples = new float[] { 100f, 200f, 300f, 400f };
        var raster = new DemRaster(2, 2, TestBounds(), samples);

        // North-west corner of bounds: lat=50 (north), lon=19 (west).
        raster.SampleBilinear(19.0, 50.0).Should().BeApproximately(100.0, 1e-9);
    }

    [Fact]
    public void SampleBilinear_AtSoutheastCornerReturnsBottomRightSample()
    {
        var samples = new float[] { 100f, 200f, 300f, 400f };
        var raster = new DemRaster(2, 2, TestBounds(), samples);

        raster.SampleBilinear(20.0, 49.0).Should().BeApproximately(400.0, 1e-9);
    }

    [Fact]
    public void SampleBilinear_AtCenterReturnsAverageOfFourCorners()
    {
        var samples = new float[] { 100f, 200f, 300f, 400f };
        var raster = new DemRaster(2, 2, TestBounds(), samples);

        // Center of bounds: midway between 19/20 lon and 49/50 lat.
        raster.SampleBilinear(19.5, 49.5).Should().BeApproximately(250.0, 1e-9);
    }

    [Fact]
    public void SampleBilinear_ClampsCoordinatesOutsideBounds()
    {
        var samples = new float[] { 100f, 200f, 300f, 400f };
        var raster = new DemRaster(2, 2, TestBounds(), samples);

        // Far west of bounds clamps to west edge; far north clamps to north edge.
        raster.SampleBilinear(0.0, 89.0).Should().BeApproximately(100.0, 1e-9);
    }

    [Fact]
    public void GetElevationRange_ExcludesNoDataValue()
    {
        var samples = new float[] { 500f, -9999f, 1500f, 2500f };
        var raster = new DemRaster(2, 2, TestBounds(), samples, noDataValue: -9999f);

        var (min, max) = raster.GetElevationRange();

        min.Should().BeApproximately(500.0, 1e-9);
        max.Should().BeApproximately(2500.0, 1e-9);
    }

    [Fact]
    public void SampleBilinear_ExcludesNoDataCornerFromBlend()
    {
        // SE corner is no-data. At the centre the four corners weigh equally, so the
        // result must be the mean of the three VALID corners (200), not a blend that
        // drags in the -9999 sentinel (which would yield a wildly negative elevation).
        var samples = new float[] { 100f, 200f, 300f, -9999f };
        var raster = new DemRaster(2, 2, TestBounds(), samples, noDataValue: -9999f);

        raster.SampleBilinear(19.5, 49.5).Should().BeApproximately(200.0, 1e-6);
    }

    [Fact]
    public void SampleBilinear_ReturnsNoDataWhenAllCornersAreNoData()
    {
        var samples = new float[] { -9999f, -9999f, -9999f, -9999f };
        var raster = new DemRaster(2, 2, TestBounds(), samples, noDataValue: -9999f);

        raster.SampleBilinear(19.5, 49.5).Should().BeApproximately(-9999.0, 1e-6);
    }
}