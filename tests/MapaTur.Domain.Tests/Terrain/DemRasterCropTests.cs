using FluentAssertions;

using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Domain.Tests.Terrain;

/// <summary>
/// <see cref="DemRaster.Crop"/> extracts a sub-grid as its own raster — the foundation for per-tile work
/// (Model 1 measures roughness and builds a mesh per cropped tile of one big z16 window). The samples and
/// the geographic sub-bounds must line up with the source so the tile lands in the same world frame.
/// </summary>
public sealed class DemRasterCropTests
{
    // 5×5, West 19.50 → East 20.40 (Δ0.90), North 49.40 → South 49.10 (Δ0.30). lon(c)=19.50+0.225c, lat(r)=49.40−0.075r.
    private static readonly MapBounds Bounds = new(new GeoPoint(49.10, 19.50), new GeoPoint(49.40, 20.40));

    private static DemRaster Ramp()
    {
        var samples = new float[25];
        for (int r = 0; r < 5; r++)
        {
            for (int c = 0; c < 5; c++)
            {
                samples[(r * 5) + c] = c + (r * 10); // distinct per cell
            }
        }

        return new DemRaster(5, 5, Bounds, samples);
    }

    [Fact]
    public void Crop_ExtractsTheSubGridSamplesAndDimensions()
    {
        DemRaster cropped = Ramp().Crop(colStart: 1, rowStart: 1, columns: 3, rows: 3);

        cropped.Columns.Should().Be(3);
        cropped.Rows.Should().Be(3);
        cropped[0, 0].Should().Be(11f, "source (col 1, row 1) = 1 + 1·10");
        cropped[2, 0].Should().Be(13f, "source (col 3, row 1)");
        cropped[2, 2].Should().Be(33f, "source (col 3, row 3)");
    }

    [Fact]
    public void Crop_GivesTheSubRegionGeographicBounds()
    {
        DemRaster cropped = Ramp().Crop(1, 1, 3, 3);

        cropped.West.Should().BeApproximately(19.725, 1e-9);  // lon(1)
        cropped.East.Should().BeApproximately(20.175, 1e-9);  // lon(3)
        cropped.North.Should().BeApproximately(49.325, 1e-9); // lat(1)
        cropped.South.Should().BeApproximately(49.175, 1e-9); // lat(3)
    }

    [Fact]
    public void Crop_PreservesNoDataValue()
    {
        var samples = new float[25];
        Array.Fill(samples, 100f);
        var raster = new DemRaster(5, 5, Bounds, samples, noDataValue: -1f);

        raster.Crop(0, 0, 2, 2).NoDataValue.Should().Be(-1f);
    }

    [Fact]
    public void Crop_WindowOutsideTheRaster_Throws()
    {
        var act = () => Ramp().Crop(colStart: 3, rowStart: 0, columns: 3, rows: 3); // 3+3 > 5

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Crop_DegenerateSize_Throws()
    {
        var act = () => Ramp().Crop(0, 0, 1, 3); // a raster needs at least 2 columns

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}