using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Nodata kafli orto (2026-07-24): GUGiK WMS wypełnia obszar poza granicą PL KRYJĄCĄ czernią RGB=(0,0,0)
/// bez kanału alfa. Detal koduje pokrycie alfą (DXT1a punch-through), więc dokładnie-czarny piksel MUSI
/// dostać alfa=0 zanim trafi do kompozycji/enkodu — inaczej przechodzi bramki `dcs.a` i maluje czarne
/// trójkąty wzdłuż granicy PL/SK (dowód: klasyfikacja shaderowa + audyt 38 kafli granicznych det25).
/// </summary>
public sealed class OrthoNodataTests
{
    private static byte[] Pixel(byte r, byte g, byte b, byte a) => new[] { r, g, b, a };

    [Fact]
    public void should_zero_alpha_on_exact_black_pixel()
    {
        byte[] rgba = Pixel(0, 0, 0, 255);

        OrthoNodata.ZeroAlphaOnBlack(rgba);

        rgba[3].Should().Be(0);
    }

    [Fact]
    public void should_keep_alpha_on_near_black_shadow_pixel()
    {
        byte[] rgba = Pixel(1, 0, 0, 255); // realny cień z lotu nigdy nie jest dokładnym zerem (lossy WebP)

        OrthoNodata.ZeroAlphaOnBlack(rgba);

        rgba[3].Should().Be(255);
    }

    [Fact]
    public void should_keep_colour_channels_untouched()
    {
        byte[] rgba = Pixel(0, 0, 0, 255);

        OrthoNodata.ZeroAlphaOnBlack(rgba);

        rgba[..3].Should().Equal(0, 0, 0); // piksel święty: korekta dotyczy WYŁĄCZNIE alfy
    }

    [Fact]
    public void should_process_every_pixel_of_a_tile()
    {
        byte[] rgba = new byte[4 * 4]; // 4 piksele: czarny, prawie-czarny, kolor, czarny
        Pixel(0, 0, 0, 255).CopyTo(rgba, 0);
        Pixel(0, 1, 0, 255).CopyTo(rgba, 4);
        Pixel(120, 140, 90, 255).CopyTo(rgba, 8);
        Pixel(0, 0, 0, 200).CopyTo(rgba, 12);

        OrthoNodata.ZeroAlphaOnBlack(rgba);

        rgba[3].Should().Be(0);
        rgba[7].Should().Be(255);
        rgba[11].Should().Be(255);
        rgba[15].Should().Be(0);
    }
}