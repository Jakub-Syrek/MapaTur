using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Rąbek nodata (2026-07-25, czarna kropkowana linia wzdłuż granicy PL): kryjąca czerń GUGiK poza granicą
/// Polski przechodzi przez STRATNY WebP, który zostawia wokół niej pierścień pikseli near-black —
/// NIE dokładnie (0,0,0), więc <see cref="OrthoNodata.ZeroAlphaOnBlack"/> ich nie łapie i wchodzą do
/// BC1 jako KRYJĄCY, czarny „teren".
///
/// ZMIERZONE w źródłowych kaflach det25 (10 kafli granicznych, pierścienie wokół dokładnej czerni):
///   1 px od czerni: mediana luma 1.0, udział luma&lt;16 = 95,3%
///   2 px:           mediana 1.3,  76,4%
///   3 px:           mediana 8.0,  51,1%
///   4 px:           mediana 90.8, 30,7%   ← tu zaczyna się prawdziwy teren
///   7 px i dalej:   mediana ~106, &lt;3%
/// kontrola (teren daleko od nodata, n=982 659): mediana 97.7, udział luma&lt;16 = 0,0%.
///
/// Dlatego rąbek gasimy WYŁĄCZNIE przez SPÓJNOŚĆ z dokładną czernią (zalew 8-sąsiedztwem), a nie samym
/// progiem jasności: prawdziwy głęboki cień w środku zdjęcia nie dotyka nodata i MUSI zostać nietknięty
/// (piksel święty — to korekta POKRYCIA, nie koloru).
/// </summary>
public sealed class OrthoNodataRimTests
{
    /// <summary>Buduje bufor RGBA (alfa 255) z mapy jasności: '#' = 0 (kryjąca czerń), cyfra = luma×10, '.' = 120.</summary>
    private static byte[] Buffer(params string[] rows)
    {
        int w = rows[0].Length, h = rows.Length;
        byte[] rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                char c = rows[y][x];
                byte v = c switch { '#' => 0, '.' => 120, _ => (byte)((c - '0') * 10) };
                int o = ((y * w) + x) * 4;
                rgba[o] = v; rgba[o + 1] = v; rgba[o + 2] = v; rgba[o + 3] = 255;
            }
        }

        return rgba;
    }

    private static byte Alpha(byte[] rgba, int w, int x, int y) => rgba[(((y * w) + x) * 4) + 3];

    [Fact]
    public void should_zero_alpha_on_exact_black_nodata()
    {
        byte[] rgba = Buffer(
            "##..",
            "##..");

        OrthoNodata.ZeroAlphaOnNodataRim(rgba, width: 4, height: 2, maxRimLuma: 16);

        Alpha(rgba, 4, 0, 0).Should().Be(0);
    }

    [Fact]
    public void should_zero_alpha_on_near_black_rim_touching_nodata()
    {
        byte[] rgba = Buffer(
            "#1..",
            "#1..");

        OrthoNodata.ZeroAlphaOnNodataRim(rgba, width: 4, height: 2, maxRimLuma: 16);

        Alpha(rgba, 4, 1, 0).Should().Be(0); // luma 10, przylega do czerni ⇒ artefakt kompresji
    }

    [Fact]
    public void should_keep_alpha_on_dark_shadow_not_touching_nodata()
    {
        byte[] rgba = Buffer(
            "#...",
            "..1.",   // ciemny piksel oddzielony jasnym terenem = PRAWDZIWY cień
            "....");

        OrthoNodata.ZeroAlphaOnNodataRim(rgba, width: 4, height: 3, maxRimLuma: 16);

        Alpha(rgba, 4, 2, 1).Should().Be(255);
    }

    [Fact]
    public void should_not_propagate_through_bright_terrain()
    {
        byte[] rgba = Buffer(
            "#1.1",   // ostatni ciemny piksel jest ZA jasnym terenem
            "....");

        OrthoNodata.ZeroAlphaOnNodataRim(rgba, width: 4, height: 2, maxRimLuma: 16);

        Alpha(rgba, 4, 3, 0).Should().Be(255);
    }

    [Fact]
    public void should_follow_the_rim_diagonally()
    {
        byte[] rgba = Buffer(
            "#...",
            ".1..",   // rąbek styka się z czernią tylko po przekątnej (8-sąsiedztwo)
            "....");

        OrthoNodata.ZeroAlphaOnNodataRim(rgba, width: 4, height: 3, maxRimLuma: 16);

        Alpha(rgba, 4, 1, 1).Should().Be(0);
    }

    [Fact]
    public void should_keep_colour_channels_untouched()
    {
        byte[] rgba = Buffer(
            "#1..",
            "....");

        OrthoNodata.ZeroAlphaOnNodataRim(rgba, width: 4, height: 2, maxRimLuma: 16);

        rgba[4].Should().Be(10); // piksel (1,0): kanał R nietknięty, zmieniona TYLKO alfa
    }

    [Fact]
    public void should_be_a_no_op_on_a_tile_without_nodata()
    {
        byte[] rgba = Buffer(
            "..1.",
            "....");

        OrthoNodata.ZeroAlphaOnNodataRim(rgba, width: 4, height: 2, maxRimLuma: 16);

        Alpha(rgba, 4, 2, 0).Should().Be(255); // bez kryjącej czerni nie ma czego zalewać
    }
}