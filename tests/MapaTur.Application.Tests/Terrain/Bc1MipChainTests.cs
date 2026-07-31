using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class Bc1MipChainTests
{
    [Fact]
    public void ByteSize_IncludesEveryMipDownToOnePixel()
    {
        Bc1MipChain.ByteSize(16).Should().Be(23 * 8);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ByteSize_RejectsNonPositiveDimensions(int pixels)
    {
        Action act = () => Bc1MipChain.ByteSize(pixels);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}