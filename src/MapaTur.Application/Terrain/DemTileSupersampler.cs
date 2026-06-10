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
}
