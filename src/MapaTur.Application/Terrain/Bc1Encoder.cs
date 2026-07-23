namespace MapaTur.Application.Terrain;

/// <summary>
/// Fast BC1 (DXT1) encoder for the GPU cell cache (2026-07-23, game-grade streaming). Ortho cells are encoded
/// ONCE — on the compose worker or in an offline bake — and every later visit is a ~15 ms disk read plus a
/// compressed upload, instead of re-decoding hundreds of WebP tiles (the measured 3–5 s per cell). Opaque
/// photographic content only: 8 bytes per 4×4 block, always the 4-colour (c0 &gt; c1) palette, endpoints by
/// principal-extreme range fit with inset — visually transparent on aerial imagery at 1/8 the RGBA size.
/// Sub-4×4 inputs (the 2×2 / 1×1 mip tail) clamp edge texels and still emit one full block, matching how
/// GL sizes compressed mip levels.
/// </summary>
public static class Bc1Encoder
{
    /// <summary>Bytes BC1 needs for a <paramref name="width"/>×<paramref name="height"/> image: one 8-byte
    /// block per started 4×4 tile (so the 1×1 mip tail still costs one block, as GL expects).</summary>
    public static int EncodedSize(int width, int height)
        => Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 8;

    /// <summary>Encodes tightly-packed RGBA8 into BC1. Alpha is ignored (ortho is opaque).</summary>
    public static void Encode(ReadOnlySpan<byte> rgba, int width, int height, Span<byte> dest)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rgba.Length, width * height * 4);
        ArgumentOutOfRangeException.ThrowIfLessThan(dest.Length, EncodedSize(width, height));

        int bw = Math.Max(1, (width + 3) / 4);
        int bh = Math.Max(1, (height + 3) / 4);
        Span<byte> px = stackalloc byte[16 * 3]; // gathered 4×4 RGB block (edge-clamped)
        for (int by = 0; by < bh; by++)
        {
            for (int bx = 0; bx < bw; bx++)
            {
                // Gather with edge clamp so mip-tail levels (and non-multiple-of-4 sizes) stay well-defined.
                for (int ty = 0; ty < 4; ty++)
                {
                    int sy = Math.Min((by * 4) + ty, height - 1);
                    for (int tx = 0; tx < 4; tx++)
                    {
                        int sx = Math.Min((bx * 4) + tx, width - 1);
                        int so = ((sy * width) + sx) * 4;
                        int po = ((ty * 4) + tx) * 3;
                        px[po] = rgba[so];
                        px[po + 1] = rgba[so + 1];
                        px[po + 2] = rgba[so + 2];
                    }
                }

                EncodeBlock(px, dest.Slice((((by * bw) + bx) * 8), 8));
            }
        }
    }

    private static void EncodeBlock(ReadOnlySpan<byte> px, Span<byte> outBlock)
    {
        // Range fit: project every texel on the block's principal extent (max−min per channel) and take the
        // two extreme texels as endpoints. For photographic content this is within a hair of PCA quality at a
        // fraction of the cost. A slight inset pulls the endpoints toward each other, which counteracts the
        // 565 quantisation snapping both extremes outward (the classic stb_dxt trick).
        int minR = 255, minG = 255, minB = 255, maxR = 0, maxG = 0, maxB = 0;
        for (int i = 0; i < 16; i++)
        {
            int r = px[i * 3], g = px[(i * 3) + 1], b = px[(i * 3) + 2];
            if (r < minR) { minR = r; }
            if (g < minG) { minG = g; }
            if (b < minB) { minB = b; }
            if (r > maxR) { maxR = r; }
            if (g > maxG) { maxG = g; }
            if (b > maxB) { maxB = b; }
        }

        int axR = maxR - minR, axG = maxG - minG, axB = maxB - minB;
        int loIdx = 0, hiIdx = 0, loDot = int.MaxValue, hiDot = int.MinValue;
        for (int i = 0; i < 16; i++)
        {
            int dot = (px[i * 3] * axR) + (px[(i * 3) + 1] * axG) + (px[(i * 3) + 2] * axB);
            if (dot < loDot) { loDot = dot; loIdx = i; }
            if (dot > hiDot) { hiDot = dot; hiIdx = i; }
        }

        // Endpoint inset by 1/16 of the range per channel (compensates 565 rounding; no-op on solid blocks).
        int e0R = px[hiIdx * 3], e0G = px[(hiIdx * 3) + 1], e0B = px[(hiIdx * 3) + 2];
        int e1R = px[loIdx * 3], e1G = px[(loIdx * 3) + 1], e1B = px[(loIdx * 3) + 2];
        int insR = (e0R - e1R) >> 4, insG = (e0G - e1G) >> 4, insB = (e0B - e1B) >> 4;
        e0R -= insR; e0G -= insG; e0B -= insB;
        e1R += insR; e1G += insG; e1B += insB;

        ushort c0 = To565(e0R, e0G, e0B);
        ushort c1 = To565(e1R, e1G, e1B);
        if (c0 == c1)
        {
            // Solid block after quantisation: indices 0 everywhere; nudge c1 down so the 4-colour path stays
            // selected when possible (c0 > c1). If c0 is 0, every texel is black either way.
            if (c0 > 0) { c1 = (ushort)(c0 - 1); }
            outBlock[0] = (byte)(c0 & 0xFF); outBlock[1] = (byte)(c0 >> 8);
            outBlock[2] = (byte)(c1 & 0xFF); outBlock[3] = (byte)(c1 >> 8);
            outBlock[4] = 0; outBlock[5] = 0; outBlock[6] = 0; outBlock[7] = 0;
            return;
        }

        if (c0 < c1)
        {
            (c0, c1) = (c1, c0);
            (e0R, e1R) = (e1R, e0R); (e0G, e1G) = (e1G, e0G); (e0B, e1B) = (e1B, e0B);
        }

        // Palette in expanded RGB, matching the decoder's arithmetic exactly.
        Span<int> palR = stackalloc int[4];
        Span<int> palG = stackalloc int[4];
        Span<int> palB = stackalloc int[4];
        (palR[0], palG[0], palB[0]) = Expand565(c0);
        (palR[1], palG[1], palB[1]) = Expand565(c1);
        palR[2] = ((2 * palR[0]) + palR[1]) / 3; palG[2] = ((2 * palG[0]) + palG[1]) / 3; palB[2] = ((2 * palB[0]) + palB[1]) / 3;
        palR[3] = (palR[0] + (2 * palR[1])) / 3; palG[3] = (palG[0] + (2 * palG[1])) / 3; palB[3] = (palB[0] + (2 * palB[1])) / 3;

        uint indices = 0;
        for (int i = 0; i < 16; i++)
        {
            int r = px[i * 3], g = px[(i * 3) + 1], b = px[(i * 3) + 2];
            int best = 0, bestD = int.MaxValue;
            for (int p = 0; p < 4; p++)
            {
                int dr = r - palR[p], dg = g - palG[p], db = b - palB[p];
                int d = (dr * dr) + (dg * dg) + (db * db);
                if (d < bestD) { bestD = d; best = p; }
            }

            indices |= (uint)best << (i * 2);
        }

        outBlock[0] = (byte)(c0 & 0xFF); outBlock[1] = (byte)(c0 >> 8);
        outBlock[2] = (byte)(c1 & 0xFF); outBlock[3] = (byte)(c1 >> 8);
        outBlock[4] = (byte)(indices & 0xFF);
        outBlock[5] = (byte)((indices >> 8) & 0xFF);
        outBlock[6] = (byte)((indices >> 16) & 0xFF);
        outBlock[7] = (byte)(indices >> 24);
    }

    // Proper rounding INTO the expanded-565 space ((v*31+127)/255, not v>>3): truncation put solid 200 at
    // expanded 206 (+6 error) where the nearest representable value is 198 (−2) — the TDD solid-block gate.
    private static ushort To565(int r, int g, int b)
        => (ushort)(((((Math.Clamp(r, 0, 255) * 31) + 127) / 255) << 11)
            | ((((Math.Clamp(g, 0, 255) * 63) + 127) / 255) << 5)
            | (((Math.Clamp(b, 0, 255) * 31) + 127) / 255));

    private static (int R, int G, int B) Expand565(ushort c)
    {
        int r = (c >> 11) & 0x1F, g = (c >> 5) & 0x3F, b = c & 0x1F;
        return ((r << 3) | (r >> 2), (g << 2) | (g >> 4), (b << 3) | (b >> 2));
    }
}
