using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour pinning for <see cref="PostProcessBufferSizing"/>: the pure dimension math that sizes the
/// half-resolution ping-pong buffers and blur mip-chain used by the bloom / god-ray post passes. Kept out
/// of the GL renderer so it is unit-testable — the renderer just consumes these sizes when it allocates
/// its offscreen textures.
/// </summary>
public sealed class PostProcessBufferSizingTests
{
    [Fact]
    public void Downsample_ByTwo_HalvesEachDimension()
    {
        var size = PostProcessBufferSizing.Downsample(1920, 1080, factor: 2);

        size.Width.Should().Be(960);
        size.Height.Should().Be(540);
    }

    [Fact]
    public void Downsample_RoundsDownOnOddDimensions()
    {
        // 1921/2 = 960.5 → 960; a post buffer can't be a fractional pixel, and rounding down keeps it
        // strictly inside the source so the upsample blit never samples outside the rendered region.
        var size = PostProcessBufferSizing.Downsample(1921, 1081, factor: 2);

        size.Width.Should().Be(960);
        size.Height.Should().Be(540);
    }

    [Fact]
    public void Downsample_FactorOne_ReturnsSourceUnchanged()
    {
        var size = PostProcessBufferSizing.Downsample(800, 600, factor: 1);

        size.Width.Should().Be(800);
        size.Height.Should().Be(600);
    }

    [Fact]
    public void Downsample_ClampsToAtLeastOnePixel()
    {
        // A 1×1 viewport divided by 4 must still yield a usable 1×1 texture, never 0 (a zero-sized
        // texture is incomplete and the FBO would fail its completeness check).
        var size = PostProcessBufferSizing.Downsample(1, 1, factor: 4);

        size.Width.Should().Be(1);
        size.Height.Should().Be(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void Downsample_RejectsNonPositiveFactor(int factor)
    {
        var act = () => PostProcessBufferSizing.Downsample(640, 480, factor);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0, 480)]
    [InlineData(640, 0)]
    [InlineData(-1, 480)]
    public void Downsample_RejectsNonPositiveViewport(int width, int height)
    {
        var act = () => PostProcessBufferSizing.Downsample(width, height, factor: 2);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void MipChainSizes_HalvesEachLevelFromTheBase()
    {
        var chain = PostProcessBufferSizing.MipChainSizes(640, 480, levels: 3);

        chain.Should().HaveCount(3);
        chain[0].Should().Be((640, 480));
        chain[1].Should().Be((320, 240));
        chain[2].Should().Be((160, 120));
    }

    [Fact]
    public void MipChainSizes_ClampsLowLevelsToOnePixel()
    {
        var chain = PostProcessBufferSizing.MipChainSizes(4, 2, levels: 4);

        chain[0].Should().Be((4, 2));
        chain[1].Should().Be((2, 1));
        chain[2].Should().Be((1, 1));
        chain[3].Should().Be((1, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void MipChainSizes_RejectsNonPositiveLevels(int levels)
    {
        var act = () => PostProcessBufferSizing.MipChainSizes(640, 480, levels);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
