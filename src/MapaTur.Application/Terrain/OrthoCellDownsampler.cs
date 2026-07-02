namespace MapaTur.Application.Terrain;

/// <summary>
/// Pure CPU box-average downsampler for ortho texture cells, applied at LOAD time so a cell never
/// becomes resident larger than a target longest side. Each baked ortho cell is an 8192×4096 RGBA8
/// image (~134 MB CPU + the same again on the GPU with mips); keeping all 16 resident is ~4.2 GB and
/// exhausts memory the moment the broad baked-tile detail ring streams in on top. Halving the longest
/// side (default 4096) cuts BOTH the CPU byte buffer AND the GL texture ~4× while staying power-of-two
/// so the renderer's mip chain still halves cleanly. The averaging is an area (box) filter so the
/// downscale is anti-aliased, not point-sampled (which would shimmer under anisotropic minification).
/// Extracted from the renderer so the resize maths is unit-testable without a GL context.
/// </summary>
public static class OrthoCellDownsampler
{
    /// <summary>Default maximum longest-side, in pixels, an ortho cell is allowed to be once resident.</summary>
    public const int DefaultMaxCellSizePx = 4096;

    /// <summary>
    /// Integer downscale factor for an image of <paramref name="width"/>×<paramref name="height"/> so its
    /// longest side does not exceed <paramref name="maxLongestSide"/>. Returns 1 when the image already fits
    /// (no resize). The factor is the smallest integer ≥ longest/max, so an 8192-wide cell at max 4096 →
    /// factor 2 (→ 4096); at max 4000 → factor 3 (→ 2731) etc. An integer factor keeps the box filter exact
    /// (each output texel averages an N×N source block).
    /// </summary>
    /// <param name="width">Source width in pixels.</param>
    /// <param name="height">Source height in pixels.</param>
    /// <param name="maxLongestSide">Target maximum for the longer of width/height (≥ 1).</param>
    /// <returns>The downscale factor (≥ 1); 1 means the cell is left untouched.</returns>
    public static int DownscaleFactor(int width, int height, int maxLongestSide)
    {
        int longest = Math.Max(width, height);
        if (longest <= 0 || maxLongestSide < 1 || longest <= maxLongestSide)
        {
            return 1;
        }

        return (int)Math.Ceiling((double)longest / maxLongestSide);
    }

    /// <summary>
    /// Box-averages <paramref name="rgba"/> (tightly packed top-row-first RGBA8, length =
    /// <paramref name="width"/>×<paramref name="height"/>×4) so its longest side does not exceed
    /// <paramref name="maxLongestSide"/>. Returns the SAME buffer + dimensions unchanged when the cell
    /// already fits or the input is invalid (so callers can assign unconditionally). Each output texel is the
    /// per-channel average of its N×N source block (N = <see cref="DownscaleFactor"/>); a partial edge block
    /// (when a dimension isn't a clean multiple of N) averages only the source texels it actually covers.
    /// </summary>
    /// <param name="rgba">Source RGBA8 pixels, top-row-first; not mutated.</param>
    /// <param name="width">Source width in pixels.</param>
    /// <param name="height">Source height in pixels.</param>
    /// <param name="maxLongestSide">Target maximum longest side in pixels (default <see cref="DefaultMaxCellSizePx"/>).</param>
    /// <returns>The (possibly downscaled) RGBA8 buffer and its new dimensions.</returns>
    public static (byte[] Rgba, int Width, int Height) Downsample(
        byte[] rgba, int width, int height, int maxLongestSide = DefaultMaxCellSizePx)
    {
        ArgumentNullException.ThrowIfNull(rgba);

        int factor = DownscaleFactor(width, height, maxLongestSide);
        if (factor <= 1 || width <= 0 || height <= 0 || rgba.Length < (long)width * height * 4)
        {
            return (rgba, width, height);
        }

        // Ceil-divide so the output covers the whole image even when a dimension isn't a multiple of factor;
        // the last row/column of output texels then average a smaller partial block.
        int outW = (width + factor - 1) / factor;
        int outH = (height + factor - 1) / factor;
        var dst = new byte[(long)outW * outH * 4];

        for (int oy = 0; oy < outH; oy++)
        {
            int srcRow0 = oy * factor;
            int srcRow1 = Math.Min(srcRow0 + factor, height);
            for (int ox = 0; ox < outW; ox++)
            {
                int srcCol0 = ox * factor;
                int srcCol1 = Math.Min(srcCol0 + factor, width);

                uint sumR = 0, sumG = 0, sumB = 0, sumA = 0;
                int count = 0;
                for (int sy = srcRow0; sy < srcRow1; sy++)
                {
                    int rowBase = (sy * width + srcCol0) * 4;
                    for (int sx = srcCol0; sx < srcCol1; sx++)
                    {
                        sumR += rgba[rowBase];
                        sumG += rgba[rowBase + 1];
                        sumB += rgba[rowBase + 2];
                        sumA += rgba[rowBase + 3];
                        rowBase += 4;
                        count++;
                    }
                }

                int di = (oy * outW + ox) * 4;
                dst[di] = (byte)(sumR / count);
                dst[di + 1] = (byte)(sumG / count);
                dst[di + 2] = (byte)(sumB / count);
                dst[di + 3] = (byte)(sumA / count);
            }
        }

        return (dst, outW, outH);
    }
}