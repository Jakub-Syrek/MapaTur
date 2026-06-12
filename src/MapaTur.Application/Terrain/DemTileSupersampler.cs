namespace MapaTur.Application.Terrain;

/// <summary>
/// Helpers that defeat the diagonal "washboard" ripple GUGiK's NMT WCS bakes into a tile when it
/// resamples its 1 m grid to a coarse requested grid (e.g. 256 px over a ~5 km z13 tile ≈ 19 m/px,
/// across an EPSG:2180→3857 reprojection). The fix is to OVER-request the tile at a denser grid — where
/// the server resamples gently — then area-average that high-resolution buffer back down to the grid the
/// mesh actually wants. Averaging is a proper low-pass, so the output is smooth at the mesh's resolution
/// with no extra vertices. The finest detail tiles (already near native 1 m) get factor 1 and are
/// untouched.
/// </summary>
public static class DemTileSupersampler
{
    /// <summary>
    /// How many times the base grid to over-request from the WCS so metres-per-pixel stays near
    /// <paramref name="targetMetersPerPixel"/>, clamped to [1, <paramref name="maxFactor"/>].
    /// </summary>
    /// <param name="tileGroundMeters">Ground width the tile covers, in metres.</param>
    /// <param name="baseTileSize">The grid size (px) the mesh ultimately consumes.</param>
    /// <param name="targetMetersPerPixel">Desired fetch resolution; ~5 m/px is gentle enough to avoid the ripple.</param>
    /// <param name="maxFactor">Upper bound on the factor (caps bandwidth/decode cost).</param>
    public static int SupersampleFactor(
        double tileGroundMeters, int baseTileSize, double targetMetersPerPixel, int maxFactor)
    {
        if (baseTileSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(baseTileSize), baseTileSize, "Must be positive.");
        }
        if (targetMetersPerPixel <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetMetersPerPixel), targetMetersPerPixel, "Must be positive.");
        }
        if (maxFactor < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFactor), maxFactor, "Must be at least 1.");
        }

        // Pixels needed for the target resolution = ground / targetMpp; divide by the base grid to get the
        // multiple, round to nearest, clamp. A small or zero tile yields factor 1 (no over-request).
        int factor = (int)Math.Round(tileGroundMeters / (targetMetersPerPixel * baseTileSize));
        return Math.Clamp(factor, 1, maxFactor);
    }

    /// <summary>
    /// Area-averages a (<paramref name="baseN"/>·<paramref name="factor"/>)² row-major grid down to
    /// <paramref name="baseN"/>², averaging each <paramref name="factor"/>×<paramref name="factor"/> block.
    /// NoData samples are excluded from a block's mean; a block with no valid samples stays NoData. Factor 1
    /// returns a copy unchanged.
    /// </summary>
    /// <param name="highRes">Row-major high-resolution samples, length (baseN·factor)².</param>
    /// <param name="baseN">Output grid size.</param>
    /// <param name="factor">Block size / over-request multiple (≥1).</param>
    /// <param name="noData">Sentinel marking missing samples.</param>
    public static float[] AreaAverageDownsample(float[] highRes, int baseN, int factor, float noData)
    {
        ArgumentNullException.ThrowIfNull(highRes);
        if (baseN <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(baseN), baseN, "Must be positive.");
        }
        if (factor < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(factor), factor, "Must be at least 1.");
        }

        int highN = baseN * factor;
        if (highRes.Length != highN * highN)
        {
            throw new ArgumentException(
                $"Expected {highN * highN} samples (baseN·factor squared), got {highRes.Length}.", nameof(highRes));
        }

        if (factor == 1)
        {
            return (float[])highRes.Clone();
        }

        var result = new float[baseN * baseN];
        for (int br = 0; br < baseN; br++)
        {
            for (int bc = 0; bc < baseN; bc++)
            {
                double sum = 0;
                int valid = 0;
                int r0 = br * factor;
                int c0 = bc * factor;
                for (int dr = 0; dr < factor; dr++)
                {
                    int rowBase = (r0 + dr) * highN;
                    for (int dc = 0; dc < factor; dc++)
                    {
                        float v = highRes[rowBase + c0 + dc];
                        if (v != noData)
                        {
                            sum += v;
                            valid++;
                        }
                    }
                }

                result[(br * baseN) + bc] = valid > 0 ? (float)(sum / valid) : noData;
            }
        }

        return result;
    }

    /// <summary>
    /// Like <see cref="AreaAverageDownsample"/> but uses a Gaussian-weighted, OVERLAPPING window instead of a
    /// disjoint box average. The plain box leaves a moiré ring-grid: disjoint blocks can't smooth across block
    /// boundaries, and the box is a leaky low-pass that passes high-frequency WCS reprojection ripple which then
    /// ALIASES to a coarse grid. A Gaussian whose σ sits at the output Nyquist removes that ripple BEFORE
    /// decimating (proper anti-alias), and the overlapping window kills the block-boundary moiré — while real
    /// terrain at the output resolution is preserved. NoData is excluded; an all-NoData neighbourhood stays
    /// NoData. Factor 1 returns a copy unchanged. Same output grid + caller's cache key as the box version, so
    /// switching the downsample needs NO re-fetch.
    /// </summary>
    /// <param name="highRes">Row-major high-resolution samples, length (baseN·factor)².</param>
    /// <param name="baseN">Output grid size.</param>
    /// <param name="factor">Over-request multiple (≥1) = the decimation ratio.</param>
    /// <param name="noData">Sentinel marking missing samples.</param>
    public static float[] LowPassDownsample(float[] highRes, int baseN, int factor, float noData)
    {
        ArgumentNullException.ThrowIfNull(highRes);
        if (baseN <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(baseN), baseN, "Must be positive.");
        }

        if (factor < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(factor), factor, "Must be at least 1.");
        }

        int highN = baseN * factor;
        if (highRes.Length != highN * highN)
        {
            throw new ArgumentException(
                $"Expected {highN * highN} samples (baseN·factor squared), got {highRes.Length}.", nameof(highRes));
        }

        if (factor == 1)
        {
            return (float[])highRes.Clone();
        }

        // σ a bit above the output Nyquist (factor·0.7) — strong enough to fully dissolve the residual moiré
        // ring-grid, still narrow enough that real z13-scale relief survives (the 1 m detail carries near-field).
        // Window ±2σ.
        double sigma = factor * 0.9;
        double twoSigmaSq = 2.0 * sigma * sigma;
        int radius = (int)Math.Ceiling(2.0 * sigma);

        var result = new float[baseN * baseN];
        for (int br = 0; br < baseN; br++)
        {
            // Block centre in high-res coordinates (fractional for even factors).
            double cr = (br * factor) + ((factor - 1) * 0.5);
            int sr0 = Math.Max(0, (int)Math.Floor(cr) - radius);
            int sr1 = Math.Min(highN - 1, (int)Math.Ceiling(cr) + radius);
            for (int bc = 0; bc < baseN; bc++)
            {
                double cc = (bc * factor) + ((factor - 1) * 0.5);
                int sc0 = Math.Max(0, (int)Math.Floor(cc) - radius);
                int sc1 = Math.Min(highN - 1, (int)Math.Ceiling(cc) + radius);

                double sum = 0;
                double wsum = 0;
                for (int sr = sr0; sr <= sr1; sr++)
                {
                    double dr = sr - cr;
                    int rowBase = sr * highN;
                    for (int sc = sc0; sc <= sc1; sc++)
                    {
                        float v = highRes[rowBase + sc];
                        if (v == noData)
                        {
                            continue;
                        }

                        double dc = sc - cc;
                        double w = Math.Exp(-((dr * dr) + (dc * dc)) / twoSigmaSq);
                        sum += v * w;
                        wsum += w;
                    }
                }

                result[(br * baseN) + bc] = wsum > 0 ? (float)(sum / wsum) : noData;
            }
        }

        return result;
    }
}