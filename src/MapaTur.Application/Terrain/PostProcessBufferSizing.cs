namespace MapaTur.Application.Terrain;

/// <summary>
/// Pure dimension math for the post-process pipeline (bloom, god rays). The blur/glow passes run at a
/// fraction of the screen resolution — softness comes for free from the upsample and the cost drops
/// quadratically — so the renderer needs to derive downsampled buffer sizes (and a halving mip-chain for
/// the blur pyramid) from the live viewport. Kept here, free of any GL dependency, so the rounding and
/// clamping rules are unit-tested rather than discovered on-device.
/// </summary>
public static class PostProcessBufferSizing
{
    /// <summary>
    /// Source viewport divided by <paramref name="factor"/>, rounded DOWN and clamped to at least 1×1.
    /// Rounding down keeps the result strictly inside the source (the upsample blit never reads outside
    /// the rendered region); the clamp guarantees a usable, framebuffer-complete texture even for a 1-pixel
    /// viewport.
    /// </summary>
    public static (int Width, int Height) Downsample(int width, int height, int factor)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(factor);

        return (Math.Max(1, width / factor), Math.Max(1, height / factor));
    }

    /// <summary>
    /// A blur-pyramid chain of <paramref name="levels"/> sizes starting at (<paramref name="width"/>,
    /// <paramref name="height"/>) and halving each successive level (rounded down, clamped to ≥1). Level 0
    /// is the base size; the bloom pass downsamples down the chain and upsamples back up.
    /// </summary>
    public static IReadOnlyList<(int Width, int Height)> MipChainSizes(int width, int height, int levels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(levels);

        var chain = new (int Width, int Height)[levels];
        int w = width;
        int h = height;
        for (int i = 0; i < levels; i++)
        {
            chain[i] = (w, h);
            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
        }

        return chain;
    }
}