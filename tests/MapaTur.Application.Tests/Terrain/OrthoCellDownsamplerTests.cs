using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour of <see cref="OrthoCellDownsampler"/>: the load-time box-average resize that keeps an ortho cell
/// from becoming resident larger than a target longest side, cutting both the retained CPU bytes and the GL
/// texture so the broad baked-tile ring doesn't exhaust memory.
/// </summary>
public sealed class OrthoCellDownsamplerTests
{
    [Theory]
    [InlineData(8192, 4096, 4096, 2)] // baked cell: longest 8192 → factor 2 → 4096×2048
    [InlineData(4096, 2048, 4096, 1)] // already fits → untouched
    [InlineData(2000, 1000, 4096, 1)] // small → untouched
    [InlineData(12000, 6000, 4096, 3)] // longest 12000 / 4096 = 2.9 → ceil 3
    public void DownscaleFactor_IsSmallestIntegerThatFitsTheLongestSide(
        int width, int height, int max, int expectedFactor)
    {
        OrthoCellDownsampler.DownscaleFactor(width, height, max).Should().Be(expectedFactor);
    }

    [Fact]
    public void Downsample_HalvesAnEightKCellToTheTargetDimensions()
    {
        const int w = 8192;
        const int h = 4096;
        var rgba = new byte[(long)w * h * 4];

        (byte[] scaled, int sw, int sh) = OrthoCellDownsampler.Downsample(rgba, w, h, maxLongestSide: 4096);

        sw.Should().Be(4096);
        sh.Should().Be(2048);
        scaled.Length.Should().Be(4096 * 2048 * 4);
        // ~4× fewer bytes than the source — the whole point of the resize.
        scaled.Length.Should().Be(rgba.Length / 4);
    }

    [Fact]
    public void Downsample_LeavesACellThatAlreadyFitsUntouched()
    {
        const int w = 2048;
        const int h = 1024;
        var rgba = new byte[w * h * 4];

        (byte[] scaled, int sw, int sh) = OrthoCellDownsampler.Downsample(rgba, w, h, maxLongestSide: 4096);

        sw.Should().Be(w);
        sh.Should().Be(h);
        scaled.Should().BeSameAs(rgba, "no resize is needed, so the same buffer flows through");
    }

    [Fact]
    public void Downsample_AveragesEachSourceBlock_PerChannel()
    {
        // A 2×2 RGBA image downscaled by factor 2 → one texel = the per-channel average of the four pixels.
        const int w = 2;
        const int h = 2;
        var rgba = new byte[]
        {
            0, 0, 0, 0,         // (0,0)
            100, 100, 100, 100, // (1,0)
            200, 200, 200, 200, // (0,1)
            40, 40, 40, 40,     // (1,1)
        };

        (byte[] scaled, int sw, int sh) = OrthoCellDownsampler.Downsample(rgba, w, h, maxLongestSide: 1);

        sw.Should().Be(1);
        sh.Should().Be(1);
        // (0 + 100 + 200 + 40) / 4 = 85 in every channel.
        scaled.Should().Equal(new byte[] { 85, 85, 85, 85 });
    }

    [Fact]
    public void Downsample_PartialEdgeBlock_AveragesOnlyTheCoveredTexels()
    {
        // 3×1 image, factor 2 → 2 output texels: the second covers only ONE source texel (the partial block),
        // so it equals that texel rather than dividing by the full block size.
        const int w = 3;
        const int h = 1;
        var rgba = new byte[]
        {
            10, 10, 10, 10,   // (0,0)
            30, 30, 30, 30,   // (1,0)
            200, 200, 200, 200, // (2,0) — alone in the partial block
        };

        (byte[] scaled, int sw, int sh) = OrthoCellDownsampler.Downsample(rgba, w, h, maxLongestSide: 2);

        sw.Should().Be(2);
        sh.Should().Be(1);
        // First texel = avg(10,30)=20; second texel = the lone 200 (not 200/2).
        scaled[0].Should().Be(20);
        scaled[4].Should().Be(200);
    }
}