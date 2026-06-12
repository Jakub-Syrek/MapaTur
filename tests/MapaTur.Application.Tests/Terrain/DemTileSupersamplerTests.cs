using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class DemTileSupersamplerTests
{
    // GUGiK WCS resamples its 1 m grid to the requested WIDTH/HEIGHT server-side. At a coarse grid
    // (256 px over a ~5 km z13 tile) that resample bakes in a diagonal "washboard" ripple; requesting a
    // denser grid makes the server resample gently. SupersampleFactor decides how many times the base
    // grid to over-request so metres-per-pixel stays near the target, capped.

    [Fact]
    public void SupersampleFactor_CoarseTile_RequestsDenserGrid()
    {
        // z13 tile ≈ 4900 m of ground; at 256 px that is ~19 m/px (washboard). Target ~5 m/px → ×4.
        int factor = DemTileSupersampler.SupersampleFactor(
            tileGroundMeters: 4900, baseTileSize: 256, targetMetersPerPixel: 5, maxFactor: 4);

        factor.Should().Be(4);
    }

    [Fact]
    public void SupersampleFactor_FineTile_RequestsAsIs()
    {
        // z16 tile ≈ 150 m of ground; at 256 px that is already ~0.6 m/px (near native) → no over-request.
        int factor = DemTileSupersampler.SupersampleFactor(
            tileGroundMeters: 150, baseTileSize: 256, targetMetersPerPixel: 5, maxFactor: 4);

        factor.Should().Be(1);
    }

    [Fact]
    public void SupersampleFactor_HugeTile_ClampedToMax()
    {
        int factor = DemTileSupersampler.SupersampleFactor(
            tileGroundMeters: 100_000, baseTileSize: 256, targetMetersPerPixel: 5, maxFactor: 4);

        factor.Should().Be(4);
    }

    [Fact]
    public void AreaAverageDownsample_FactorTwo_AveragesEachBlock()
    {
        // 4×4 high-res grid (baseN=2, factor=2), row-major.
        float[] highRes =
        {
            1, 2, 3, 4,
            5, 6, 7, 8,
            9, 10, 11, 12,
            13, 14, 15, 16,
        };

        float[] result = DemTileSupersampler.AreaAverageDownsample(highRes, baseN: 2, factor: 2, noData: -32768f);

        // Block (0,0)={1,2,5,6}=3.5  (0,1)={3,4,7,8}=5.5  (1,0)={9,10,13,14}=11.5  (1,1)={11,12,15,16}=13.5
        var expected = new[] { 3.5f, 5.5f, 11.5f, 13.5f };
        result.Should().Equal(expected);
    }

    [Fact]
    public void AreaAverageDownsample_ExcludesNoDataFromBlockMean()
    {
        // Single 2×2 block (baseN=1, factor=2) with one NoData sample → mean of the three valid ones.
        float[] highRes = { 10f, -32768f, 20f, 30f };

        float[] result = DemTileSupersampler.AreaAverageDownsample(highRes, baseN: 1, factor: 2, noData: -32768f);

        result.Should().HaveCount(1);
        result[0].Should().BeApproximately(20f, 0.001f); // (10+20+30)/3
    }

    [Fact]
    public void AreaAverageDownsample_AllNoDataBlock_StaysNoData()
    {
        float[] highRes = { -32768f, -32768f, -32768f, -32768f };

        float[] result = DemTileSupersampler.AreaAverageDownsample(highRes, baseN: 1, factor: 2, noData: -32768f);

        result[0].Should().Be(-32768f);
    }

    [Fact]
    public void AreaAverageDownsample_FactorOne_ReturnsCopyOfInput()
    {
        float[] highRes = { 1f, 2f, 3f, 4f };

        float[] result = DemTileSupersampler.AreaAverageDownsample(highRes, baseN: 2, factor: 1, noData: -32768f);

        result.Should().Equal(highRes);
    }

    // LowPassDownsample replaces the box average: a Gaussian-weighted, OVERLAPPING window that removes the
    // moiré ring-grid the box left (disjoint blocks + leaky low-pass → aliased WCS ripple = "obwódki").

    [Fact]
    public void LowPassDownsample_ConstantGrid_ReturnsConstant()
    {
        // Flat terrain must stay flat (no distortion), edges included.
        var highRes = new float[8 * 8];
        Array.Fill(highRes, 100f);

        float[] result = DemTileSupersampler.LowPassDownsample(highRes, baseN: 4, factor: 2, noData: -32768f);

        result.Should().HaveCount(16);
        foreach (float v in result)
        {
            v.Should().BeApproximately(100f, 0.001f);
        }
    }

    [Fact]
    public void LowPassDownsample_FactorOne_ReturnsCopyOfInput()
    {
        float[] highRes = { 1f, 2f, 3f, 4f };

        float[] result = DemTileSupersampler.LowPassDownsample(highRes, baseN: 2, factor: 1, noData: -32768f);

        result.Should().Equal(highRes);
    }

    [Fact]
    public void LowPassDownsample_AllNoDataNeighbourhood_StaysNoData()
    {
        var highRes = new float[4 * 4];
        Array.Fill(highRes, -32768f);

        float[] result = DemTileSupersampler.LowPassDownsample(highRes, baseN: 2, factor: 2, noData: -32768f);

        result.Should().OnlyContain(v => v == -32768f);
    }

    [Fact]
    public void LowPassDownsample_OverlapsBlocks_SpreadingAcrossBoundaries_UnlikeBox()
    {
        // A single high-res spike. The box average confines it to its one block; the Gaussian window overlaps
        // neighbouring blocks, so the spike's energy bleeds into an ADJACENT output cell — that overlap is what
        // dissolves the block-boundary moiré. baseN=3, factor=2 → highN=6; spike at (row 3, col 3).
        var highRes = new float[6 * 6];
        highRes[(3 * 6) + 3] = 100f;

        float[] box = DemTileSupersampler.AreaAverageDownsample(highRes, baseN: 3, factor: 2, noData: -32768f);
        float[] low = DemTileSupersampler.LowPassDownsample(highRes, baseN: 3, factor: 2, noData: -32768f);

        // Output cell (row 2, col 1): the box keeps it 0 (different block); the Gaussian leaks the spike into it.
        int adjacent = (2 * 3) + 1;
        box[adjacent].Should().Be(0f);
        low[adjacent].Should().BeGreaterThan(0f);
    }
}