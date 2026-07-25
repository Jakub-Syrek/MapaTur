using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// BC1 (DXT1) block compression for the GPU cell cache (2026-07-23, game-grade streaming): ortho cells are
/// encoded ONCE (compose worker / bake) and every later visit is a ~15 ms disk read + compressed upload
/// instead of a 3–5 s WebP decode storm. Pure CPU encoder, 8 bytes per 4×4 block, no alpha (ortho is opaque).
/// The tests verify against a reference BC1 decoder: exactness on solid blocks (within RGB565 quantisation),
/// extreme preservation on two-colour blocks, bounded RMSE on photographic gradients, and edge clamping for
/// the sub-4×4 mip tail (2×2, 1×1 still occupy one full block).
/// </summary>
public sealed class Bc1EncoderTests
{
    [Fact]
    public void EncodedSize_8x8_IsFourBlocks()
    {
        Bc1Encoder.EncodedSize(8, 8).Should().Be(4 * 8);
    }

    [Fact]
    public void EncodedSize_MipTail_1x1_IsOneBlock()
    {
        Bc1Encoder.EncodedSize(1, 1).Should().Be(8);
    }

    [Fact]
    public void Encode_SolidColor_RoundTripsWithin565Quantisation()
    {
        byte[] rgba = SolidRgba(4, 4, r: 200, g: 64, b: 120);
        byte[] dest = new byte[Bc1Encoder.EncodedSize(4, 4)];

        Bc1Encoder.Encode(rgba, 4, 4, dest);
        (byte r, byte g, byte b) = DecodeTexel(dest, 4, 4, x: 1, y: 2);

        // RGB565 quantisation: ±4 on R/B (5 bits), ±2 on G (6 bits) — plus 1 for rounding slack.
        Math.Abs(r - 200).Should().BeLessThanOrEqualTo(5);
        Math.Abs(g - 64).Should().BeLessThanOrEqualTo(3);
        Math.Abs(b - 120).Should().BeLessThanOrEqualTo(5);
    }

    [Fact]
    public void Encode_TwoColourBlock_PreservesBothExtremes()
    {
        // Left half near-black rock shadow, right half near-white limestone — the endpoints must not collapse.
        byte[] rgba = new byte[4 * 4 * 4];
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                byte v = x < 2 ? (byte)10 : (byte)245;
                int o = ((y * 4) + x) * 4;
                rgba[o] = v; rgba[o + 1] = v; rgba[o + 2] = v; rgba[o + 3] = 255;
            }
        }

        byte[] dest = new byte[Bc1Encoder.EncodedSize(4, 4)];
        Bc1Encoder.Encode(rgba, 4, 4, dest);

        (byte darkR, _, _) = DecodeTexel(dest, 4, 4, x: 0, y: 0);
        (byte liteR, _, _) = DecodeTexel(dest, 4, 4, x: 3, y: 0);
        darkR.Should().BeLessThan(40, "the dark extreme must stay dark");
        liteR.Should().BeGreaterThan(215, "the light extreme must stay light");
    }

    [Fact]
    public void Encode_Gradient_KeepsRmseBelowPhotographicThreshold()
    {
        const int W = 16, H = 16;
        byte[] rgba = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                int o = ((y * W) + x) * 4;
                rgba[o] = (byte)(x * 16);          // R ramp
                rgba[o + 1] = (byte)(96 + (y * 8)); // G ramp
                rgba[o + 2] = 90;                   // constant B
                rgba[o + 3] = 255;
            }
        }

        byte[] dest = new byte[Bc1Encoder.EncodedSize(W, H)];
        Bc1Encoder.Encode(rgba, W, H, dest);

        double sq = 0;
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                (byte r, byte g, byte b) = DecodeTexel(dest, W, H, x, y);
                int o = ((y * W) + x) * 4;
                sq += ((r - rgba[o]) * (r - rgba[o]))
                    + ((g - rgba[o + 1]) * (g - rgba[o + 1]))
                    + ((b - rgba[o + 2]) * (b - rgba[o + 2]));
            }
        }

        double rmse = Math.Sqrt(sq / (W * H * 3));
        rmse.Should().BeLessThan(8.0, "BC1 range-fit on smooth photographic ramps stays visually transparent");
    }

    [Fact]
    public void Encode_2x2_MipTail_ClampsEdgesAndRoundTrips()
    {
        byte[] rgba = SolidRgba(2, 2, r: 80, g: 160, b: 40);
        byte[] dest = new byte[Bc1Encoder.EncodedSize(2, 2)];

        Bc1Encoder.Encode(rgba, 2, 2, dest);
        (byte r, byte g, byte b) = DecodeTexel(dest, 2, 2, x: 1, y: 1);

        Math.Abs(r - 80).Should().BeLessThanOrEqualTo(5);
        Math.Abs(g - 160).Should().BeLessThanOrEqualTo(3);
        Math.Abs(b - 40).Should().BeLessThanOrEqualTo(5);
    }

    [Fact]
    public void Encode_TransparentBlock_UsesPunchThroughAlpha()
    {
        // Regresja 2026-07-23 („czarne dziury"): RGBA miało alpha=0 na niepokrytych obszarach celi i baza
        // prześwitywała; BC1-RGB malował je czernią. Blok w pełni przezroczysty MUSI wyjść w trybie
        // 3-kolorowym (c0 <= c1) z indeksami 3 = przezroczysta czerń (DXT1a punch-through).
        byte[] rgba = new byte[4 * 4 * 4]; // wszystko 0, w tym alpha
        byte[] dest = new byte[Bc1Encoder.EncodedSize(4, 4)];

        Bc1Encoder.Encode(rgba, 4, 4, dest);

        ushort c0 = (ushort)(dest[0] | (dest[1] << 8));
        ushort c1 = (ushort)(dest[2] | (dest[3] << 8));
        (c0 <= c1).Should().BeTrue("tryb 3-kolorowy sygnalizuje przezroczystość w DXT1a");
        uint idx = (uint)(dest[4] | (dest[5] << 8) | (dest[6] << 16) | (dest[7] << 24));
        idx.Should().Be(0xFFFFFFFFu, "każdy texel = indeks 3 = przezroczysty");
    }

    [Fact]
    public void Encode_MixedAlphaBlock_KeepsOpaqueColourAndTransparentHoles()
    {
        // Lewa połowa kryjąca zieleń, prawa przezroczysta (brzeg pokrycia celi).
        byte[] rgba = new byte[4 * 4 * 4];
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 2; x++)
            {
                int o = ((y * 4) + x) * 4;
                rgba[o] = 40; rgba[o + 1] = 180; rgba[o + 2] = 60; rgba[o + 3] = 255;
            }
        }

        byte[] dest = new byte[Bc1Encoder.EncodedSize(4, 4)];
        Bc1Encoder.Encode(rgba, 4, 4, dest);

        uint idx = (uint)(dest[4] | (dest[5] << 8) | (dest[6] << 16) | (dest[7] << 24));
        for (int t = 0; t < 16; t++)
        {
            int sel = (int)((idx >> (t * 2)) & 0x3);
            bool transparent = (t % 4) >= 2;
            if (transparent)
            {
                sel.Should().Be(3, "przezroczysty texel = indeks 3");
            }
            else
            {
                sel.Should().NotBe(3, "kryjący texel nie może wpaść w przezroczystość");
            }
        }
    }

    private static byte[] SolidRgba(int w, int h, byte r, byte g, byte b)
    {
        byte[] rgba = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            rgba[i * 4] = r; rgba[(i * 4) + 1] = g; rgba[(i * 4) + 2] = b; rgba[(i * 4) + 3] = 255;
        }

        return rgba;
    }

    // Reference BC1 decoder (test-only): enough of the spec to verify the encoder — 565 endpoints, the
    // 4-colour palette (c0 > c1 path our opaque encoder always emits), 2-bit indices.
    private static (byte R, byte G, byte B) DecodeTexel(byte[] bc1, int width, int height, int x, int y)
    {
        int bw = Math.Max(1, (width + 3) / 4);
        int block = ((y / 4) * bw) + (x / 4);
        int o = block * 8;
        ushort c0 = (ushort)(bc1[o] | (bc1[o + 1] << 8));
        ushort c1 = (ushort)(bc1[o + 2] | (bc1[o + 3] << 8));
        uint idx = (uint)(bc1[o + 4] | (bc1[o + 5] << 8) | (bc1[o + 6] << 16) | (bc1[o + 7] << 24));

        Span<(int R, int G, int B)> pal = stackalloc (int, int, int)[4];
        pal[0] = Expand565(c0);
        pal[1] = Expand565(c1);
        if (c0 > c1)
        {
            pal[2] = (((2 * pal[0].R) + pal[1].R) / 3, ((2 * pal[0].G) + pal[1].G) / 3, ((2 * pal[0].B) + pal[1].B) / 3);
            pal[3] = ((pal[0].R + (2 * pal[1].R)) / 3, (pal[0].G + (2 * pal[1].G)) / 3, (pal[0].B + (2 * pal[1].B)) / 3);
        }
        else
        {
            pal[2] = ((pal[0].R + pal[1].R) / 2, (pal[0].G + pal[1].G) / 2, (pal[0].B + pal[1].B) / 2);
            pal[3] = (0, 0, 0);
        }

        int texel = ((y % 4) * 4) + (x % 4);
        int sel = (int)((idx >> (texel * 2)) & 0x3);
        return ((byte)pal[sel].R, (byte)pal[sel].G, (byte)pal[sel].B);
    }

    private static (int R, int G, int B) Expand565(ushort c)
    {
        int r = (c >> 11) & 0x1F, g = (c >> 5) & 0x3F, b = c & 0x1F;
        return ((r << 3) | (r >> 2), (g << 2) | (g >> 4), (b << 3) | (b >> 2));
    }
}
