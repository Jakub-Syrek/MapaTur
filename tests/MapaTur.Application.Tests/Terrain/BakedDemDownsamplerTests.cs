using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Pins <see cref="BakedDemDownsampler"/>, the NoData-aware box-average reducer that turns a baked z16 tile
/// into the coarser LOD levels of the offline pyramid. Unlike the renderer's nearest-neighbour decimation,
/// the bake averages so coarse levels keep an honest mean elevation; a coarse cell is NoData only when EVERY
/// source cell feeding it is NoData, otherwise it averages the valid ones. Same bounds, halved (1/factor)
/// resolution, fully deterministic.
/// </summary>
public sealed class BakedDemDownsamplerTests
{
    private static MapBounds Bounds() => new(new GeoPoint(49.0, 20.0), new GeoPoint(49.1, 20.1));

    private static BakedDemTile Tile(int cols, int rows, float[] heights, double noData = -9999.0) =>
        new(16, 100, 200, cols, rows, Bounds(), noData, heights);

    [Fact]
    public void Downsample_HalvesDimensionsForAnEvenGrid()
    {
        var heights = new float[8 * 6];
        Array.Fill(heights, 1000f);
        BakedDemTile src = Tile(8, 6, heights);

        BakedDemTile result = BakedDemDownsampler.Downsample(src, factor: 2);

        result.Columns.Should().Be(4);
        result.Rows.Should().Be(3);
    }

    [Fact]
    public void Downsample_RoundsDimensionsUpForAnOddGrid()
    {
        var heights = new float[7 * 5];
        Array.Fill(heights, 1000f);
        BakedDemTile src = Tile(7, 5, heights);

        BakedDemTile result = BakedDemDownsampler.Downsample(src, factor: 2);

        result.Columns.Should().Be(4); // ceil(7/2)
        result.Rows.Should().Be(3);    // ceil(5/2)
    }

    [Fact]
    public void Downsample_AveragesThe2x2BlockOfValidCells()
    {
        // One 2x2 block: 10, 20, 30, 40 → mean 25.
        var heights = new float[] { 10f, 20f, 30f, 40f };
        BakedDemTile src = Tile(2, 2, heights);

        BakedDemTile result = BakedDemDownsampler.Downsample(src, factor: 2);

        result.Columns.Should().Be(1);
        result.Heights[0].Should().BeApproximately(25f, 1e-4f);
    }

    [Fact]
    public void Downsample_AveragesOnlyValidCellsWhenSomeAreNoData()
    {
        // Block of 10, NoData, 30, 40 → mean of the three valid = 26.6667.
        var heights = new float[] { 10f, -9999f, 30f, 40f };
        BakedDemTile src = Tile(2, 2, heights);

        BakedDemTile result = BakedDemDownsampler.Downsample(src, factor: 2);

        result.Heights[0].Should().BeApproximately((10f + 30f + 40f) / 3f, 1e-4f);
    }

    [Fact]
    public void Downsample_ProducesNoDataOnlyWhenEverySourceCellIsNoData()
    {
        var heights = new float[] { -9999f, -9999f, -9999f, -9999f };
        BakedDemTile src = Tile(2, 2, heights);

        BakedDemTile result = BakedDemDownsampler.Downsample(src, factor: 2);

        result.Heights[0].Should().Be(-9999f);
    }

    [Fact]
    public void Downsample_PreservesBoundsAndNoDataSentinel()
    {
        var heights = new float[4 * 4];
        Array.Fill(heights, 1234f);
        BakedDemTile src = Tile(4, 4, heights, noData: -32768.0);

        BakedDemTile result = BakedDemDownsampler.Downsample(src, factor: 2);

        result.Bounds.Should().Be(src.Bounds);
        result.NoDataValue.Should().Be(-32768.0);
    }

    [Fact]
    public void Downsample_IsDeterministic()
    {
        var heights = new float[6 * 6];
        for (int i = 0; i < heights.Length; i++)
        {
            heights[i] = (i * 7) % 53;
        }

        BakedDemTile src = Tile(6, 6, heights);

        BakedDemTile a = BakedDemDownsampler.Downsample(src, factor: 2);
        BakedDemTile b = BakedDemDownsampler.Downsample(src, factor: 2);

        b.Heights.Should().Equal(a.Heights);
    }

    [Fact]
    public void Downsample_RejectsAFactorBelowTwo()
    {
        var heights = new float[4 * 4];
        BakedDemTile src = Tile(4, 4, heights);

        var act = () => BakedDemDownsampler.Downsample(src, factor: 1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}