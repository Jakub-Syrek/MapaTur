using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class TerrariumDecoderTests
{
    [Fact]
    public void DecodePixel_ZeroMetres_Is128_0_0()
    {
        // Terrarium: elevation = (R*256 + G + B/256) - 32768. Sea level → R=128,G=0,B=0.
        TerrariumDecoder.DecodePixel(128, 0, 0).Should().BeApproximately(0f, 0.001f);
    }

    [Fact]
    public void DecodePixel_OneMetre()
    {
        TerrariumDecoder.DecodePixel(128, 1, 0).Should().BeApproximately(1f, 0.001f);
    }

    [Fact]
    public void DecodePixel_HalfMetreFromBlue()
    {
        TerrariumDecoder.DecodePixel(128, 0, 128).Should().BeApproximately(0.5f, 0.001f);
    }

    [Fact]
    public void DecodePixel_Minimum_IsNegative32768()
    {
        TerrariumDecoder.DecodePixel(0, 0, 0).Should().BeApproximately(-32768f, 0.001f);
    }

    [Fact]
    public void DecodePixel_MountainHeight()
    {
        // ~2499 m (Rysy-ish): 32768 + 2499 = 35267 = R*256 + G → R=137, G=195 (137*256=35072, +195=35267).
        TerrariumDecoder.DecodePixel(137, 195, 0).Should().BeApproximately(2499f, 0.001f);
    }

    [Fact]
    public void Decode_NullPixels_Throws()
    {
        Action act = () => TerrariumDecoder.Decode(null!, 2, 2);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Decode_BufferTooSmall_Throws()
    {
        Action act = () => TerrariumDecoder.Decode(new byte[4], 2, 2); // needs 2*2*4=16

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Decode_FillsGridRowMajor()
    {
        // 2x1 image: pixel0 = 0 m (128,0,0), pixel1 = 1 m (128,1,0). RGBA8.
        byte[] rgba =
        {
            128, 0, 0, 255,
            128, 1, 0, 255,
        };

        float[] elevations = TerrariumDecoder.Decode(rgba, 2, 1);

        elevations.Should().HaveCount(2);
        elevations[0].Should().BeApproximately(0f, 0.001f);
        elevations[1].Should().BeApproximately(1f, 0.001f);
    }
}