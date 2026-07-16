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

    // σ of the anti-alias Gaussian per unit of decimation factor — a bit above the output Nyquist: strong
    // enough to fully dissolve the residual moiré ring-grid, still narrow enough that real relief survives.
    // Single-sourced so the kernel and LowPassKernelRadius (the padding width callers must supply to keep the
    // window off the tile edge) can never drift apart.
    private const double SigmaPerFactor = 0.9;

    /// <summary>
    /// The Gaussian window's reach in hi-res pixels (±2σ) for a given decimation <paramref name="factor"/> —
    /// the basis of the neighbour padding <see cref="LowPassDownsampleToNodes"/> needs (radius + 1, because a
    /// node-registered edge sample sits half a hi-px outside the tile) so no output sample's window clamps at
    /// the tile edge. An unpadded edge clamp shifts the outer row's sampling centroid into the tile, which on
    /// a slope is a real height kink along every tile border (measured on the baked z17 pyramid: p95
    /// |curvature residual| ≈ 1.0 m at ±1 cell vs 0.44 m background).
    /// </summary>
    /// <param name="factor">Over-request multiple (≥1) = the decimation ratio.</param>
    public static int LowPassKernelRadius(int factor)
    {
        if (factor < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(factor), factor, "Must be at least 1.");
        }

        return (int)Math.Ceiling(2.0 * (SigmaPerFactor * factor));
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

        double sigma = factor * SigmaPerFactor;
        double twoSigmaSq = 2.0 * sigma * sigma;
        int radius = (int)Math.Ceiling(2.0 * sigma);

        var result = new float[baseN * baseN];
        for (int br = 0; br < baseN; br++)
        {
            // Block centre in high-res coordinates (fractional for even factors). NB: read-as-nodes this
            // mapping is squeezed (see LowPassDownsampleToNodes, which the z17 bake path uses instead) —
            // kept for legacy/diagnostic callers that expect the block-centre semantics.
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

    /// <summary>
    /// The NODE-REGISTERED Gaussian downsample over a neighbour-padded buffer (see
    /// <see cref="PadWithNeighbours"/>): output sample <c>j</c> is taken AT the node position the pipeline
    /// reads it as — ground <c>j/(baseN−1)</c> of the tile, i.e. hi-res lattice position
    /// <c>j·(baseN·factor)/(baseN−1) − 0.5</c> (the WCS hi-res grid is pixel-CENTRE over the tile bbox).
    ///
    /// Why not block centres: a block-centre downsample puts sample <c>j</c> at hi position
    /// <c>j·factor + (factor−1)/2</c>, which read-as-nodes SQUEEZES every tile's content ~half a hi-px toward
    /// its centre — adjacent tiles' content then SEPARATES by ~one hi-px at every border and the baker's weld
    /// bridges the gap, leaving a slope-proportional crease along every tile border (measured on the real z17
    /// pyramid as the ±1-cell p95 excess the border gate flags). At the node positions the squeeze is zero,
    /// and the border node of two adjacent tiles reads the IDENTICAL global window through either tile's
    /// padded buffer — bit-identical values by construction, so the weld becomes a no-op.
    ///
    /// Sentinel padding (a missing neighbour) is excluded by the NoData rule and the weights renormalise —
    /// the deterministic clamp equivalent on that side.
    /// </summary>
    /// <param name="highRes">Row-major samples, length (baseN·factor + 2·pad)².</param>
    /// <param name="baseN">Output grid size (the core's base resolution, read as pixel-is-point nodes).</param>
    /// <param name="factor">Over-request multiple (≥1) = the decimation ratio.</param>
    /// <param name="noData">Sentinel marking missing samples.</param>
    /// <param name="pad">Padding width in hi-res pixels on each side; must be ≥
    /// <see cref="LowPassKernelRadius"/>(<paramref name="factor"/>) + 1, because an edge node sits half a
    /// hi-px OUTSIDE the tile and its window must not clamp (that would re-introduce the squeeze).</param>
    public static float[] LowPassDownsampleToNodes(float[] highRes, int baseN, int factor, float noData, int pad)
    {
        ArgumentNullException.ThrowIfNull(highRes);
        if (baseN <= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(baseN), baseN, "Node registration needs at least 2 nodes.");
        }

        if (factor < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(factor), factor, "Must be at least 1.");
        }

        double sigma = factor * SigmaPerFactor;
        double twoSigmaSq = 2.0 * sigma * sigma;
        int radius = (int)Math.Ceiling(2.0 * sigma);
        if (pad < radius + 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pad), pad,
                $"Node-registered sampling needs pad ≥ kernel radius + 1 ({radius + 1}) — an edge node sits half "
                + "a hi-px outside the tile and a clamped window would re-introduce the border squeeze.");
        }

        int highN = baseN * factor;
        int paddedN = highN + (2 * pad);
        if (highRes.Length != paddedN * paddedN)
        {
            throw new ArgumentException(
                $"Expected {paddedN * paddedN} samples ((baseN·factor + 2·pad) squared), got {highRes.Length}.",
                nameof(highRes));
        }

        var result = new float[baseN * baseN];
        for (int br = 0; br < baseN; br++)
        {
            // The node's position on the (padded) hi-res pixel-centre lattice — an exact dyadic-free mapping
            // shared by BOTH tiles that own a border node, so their windows tap the same global cells.
            double cr = pad + (br * (double)highN / (baseN - 1)) - 0.5;
            int sr0 = (int)Math.Floor(cr) - radius;
            int sr1 = (int)Math.Ceiling(cr) + radius;
            for (int bc = 0; bc < baseN; bc++)
            {
                double cc = pad + (bc * (double)highN / (baseN - 1)) - 0.5;
                int sc0 = (int)Math.Floor(cc) - radius;
                int sc1 = (int)Math.Ceiling(cc) + radius;

                double sum = 0;
                double wsum = 0;
                for (int sr = sr0; sr <= sr1; sr++)
                {
                    double dr = sr - cr;
                    int rowBase = sr * paddedN;
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

    /// <summary>How much neighbour padding <see cref="ResampleToNodes"/> needs: the Catmull-Rom footprint
    /// reaches 2 cells past a node position (and an edge node sits half a cell outside the tile).</summary>
    public const int NativeNodePadCells = 2;

    /// <summary>
    /// NODE-REGISTERS a NATIVE-resolution pixel-centre grid (the z16 fetches and the injected legacy DMR5
    /// tiles): sample <c>j</c> of the output is the Catmull-Rom value AT node position
    /// <c>j·N/(N−1) − 0.5</c> on the (neighbour-padded) pixel-centre lattice. Same registration flaw and
    /// same cure as the z17 supersample (<see cref="LowPassDownsampleToNodes"/>) — measured on the baked
    /// pyramid as z16 border p95 1.69 m vs 1.17 m control and the SK↔SK ±4 cm median kink — but with an
    /// INTERPOLATING kernel instead of a low-pass: native data carries the finest real detail the pyramid
    /// has, and a Gaussian here would blur it. Catmull-Rom has exact linear precision (planes are preserved
    /// bit-for-bit up to float noise) and the border node of two adjacent tiles reads the identical global
    /// 4×4 footprint from either side — bit-identical, so the baker's weld is a no-op.
    ///
    /// A footprint touching NoData degrades to a NoData-aware bilinear over the inner 2×2 (a sentinel under
    /// a negative cubic weight would poison the surface); an all-invalid footprint stays NoData.
    /// </summary>
    /// <param name="padded">Row-major (baseN + 2·pad)² pixel-centre samples (core + neighbour ring).</param>
    /// <param name="baseN">The tile's grid side; the output is baseN² node-registered samples.</param>
    /// <param name="noData">Sentinel marking missing samples.</param>
    /// <param name="pad">Padding width in cells; must be ≥ <see cref="NativeNodePadCells"/>.</param>
    public static float[] ResampleToNodes(float[] padded, int baseN, float noData, int pad)
    {
        ArgumentNullException.ThrowIfNull(padded);
        if (baseN <= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(baseN), baseN, "Node registration needs at least 2 nodes.");
        }

        if (pad < NativeNodePadCells)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pad), pad,
                $"Node-registered Catmull-Rom needs pad ≥ {NativeNodePadCells} — an edge node sits half a cell "
                + "outside the tile and its 4×4 footprint must not clamp.");
        }

        int n = baseN + (2 * pad);
        if (padded.Length != n * n)
        {
            throw new ArgumentException(
                $"Expected {n * n} samples ((baseN + 2·pad) squared), got {padded.Length}.", nameof(padded));
        }

        var result = new float[baseN * baseN];
        for (int j = 0; j < baseN; j++)
        {
            double cy = pad + (j * (double)baseN / (baseN - 1)) - 0.5;
            for (int i = 0; i < baseN; i++)
            {
                double cx = pad + (i * (double)baseN / (baseN - 1)) - 0.5;
                result[(j * baseN) + i] = SampleCatmullRomGrid(padded, n, cx, cy, noData, clampTaps: false);
            }
        }

        return result;
    }

    /// <summary>
    /// Catmull-Rom upsample of a pixel-centre grid onto a FINER pixel-centre lattice over the same extent
    /// (fine sample <c>i</c> at coarse-lattice position <c>(i+0.5)·srcN/dstN − 0.5</c>). Used to synthesise a
    /// hi-res padding strip from a LEGACY 256 px neighbour (the injected DMR5 tiles have no 512 px tif), so a
    /// supersampled tile's kernel window doesn't clamp at a PL↔SK border. Taps clamp at the source edges (no
    /// further neighbour available) — the strip's own outer margin, far from the border being fixed.
    /// </summary>
    /// <param name="source">Row-major srcN² pixel-centre samples.</param>
    /// <param name="srcN">Source grid side.</param>
    /// <param name="dstN">Destination grid side (&gt; srcN).</param>
    /// <param name="noData">Sentinel marking missing samples (degrades the footprint to bilinear).</param>
    public static float[] UpsamplePixelCentreGrid(float[] source, int srcN, int dstN, float noData)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (srcN <= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(srcN), srcN, "Need at least a 2×2 source.");
        }

        if (dstN <= srcN)
        {
            throw new ArgumentOutOfRangeException(nameof(dstN), dstN, "Destination must be finer than the source.");
        }

        if (source.Length != srcN * srcN)
        {
            throw new ArgumentException($"Expected {srcN * srcN} samples, got {source.Length}.", nameof(source));
        }

        var result = new float[dstN * dstN];
        double scale = (double)srcN / dstN;
        for (int j = 0; j < dstN; j++)
        {
            double cy = ((j + 0.5) * scale) - 0.5;
            for (int i = 0; i < dstN; i++)
            {
                double cx = ((i + 0.5) * scale) - 0.5;
                result[(j * dstN) + i] = SampleCatmullRomGrid(source, srcN, cx, cy, noData, clampTaps: true);
            }
        }

        return result;
    }

    // Separable Catmull-Rom over a square grid at fractional (cx, cy); NoData in the 4×4 footprint degrades
    // to a NoData-aware renormalised bilinear over the inner 2×2 (negative cubic weights must never touch a
    // sentinel); an all-invalid inner quad returns noData. clampTaps clamps the footprint at the grid edge
    // (the upsample path); the node path forbids it by construction (pad ≥ taps' reach).
    private static float SampleCatmullRomGrid(float[] grid, int n, double cx, double cy, float noData, bool clampTaps)
    {
        int c1 = (int)Math.Floor(cx);
        int r1 = (int)Math.Floor(cy);
        double tx = cx - c1;
        double ty = cy - r1;

        Span<double> rows = stackalloc double[4];
        Span<double> taps = stackalloc double[4];
        bool degraded = false;
        for (int dr = 0; dr < 4 && !degraded; dr++)
        {
            int r = r1 - 1 + dr;
            if (clampTaps)
            {
                r = Math.Clamp(r, 0, n - 1);
            }

            for (int dc = 0; dc < 4; dc++)
            {
                int c = c1 - 1 + dc;
                if (clampTaps)
                {
                    c = Math.Clamp(c, 0, n - 1);
                }

                float v = grid[(r * n) + c];
                if (v == noData)
                {
                    degraded = true;
                    break;
                }

                taps[dc] = v;
            }

            if (!degraded)
            {
                rows[dr] = CatmullRom(taps[0], taps[1], taps[2], taps[3], tx);
            }
        }

        if (!degraded)
        {
            return (float)CatmullRom(rows[0], rows[1], rows[2], rows[3], ty);
        }

        // Renormalised bilinear over the inner 2×2 — the NoData-aware fallback.
        double sum = 0;
        double wsum = 0;
        for (int dr = 0; dr < 2; dr++)
        {
            int r = r1 + dr;
            if (clampTaps)
            {
                r = Math.Clamp(r, 0, n - 1);
            }

            double wr = dr == 0 ? 1.0 - ty : ty;
            for (int dc = 0; dc < 2; dc++)
            {
                int c = c1 + dc;
                if (clampTaps)
                {
                    c = Math.Clamp(c, 0, n - 1);
                }

                float v = grid[(r * n) + c];
                if (v == noData)
                {
                    continue;
                }

                double w = wr * (dc == 0 ? 1.0 - tx : tx);
                sum += v * w;
                wsum += w;
            }
        }

        return wsum > 0 ? (float)(sum / wsum) : noData;
    }

    // Uniform Catmull-Rom: interpolates p1..p2 for t in [0,1], C1-continuous, exact linear precision.
    private static double CatmullRom(double p0, double p1, double p2, double p3, double t)
    {
        double t2 = t * t;
        double t3 = t2 * t;
        return 0.5 * ((2.0 * p1)
            + ((p2 - p0) * t)
            + (((2.0 * p0) - (5.0 * p1) + (4.0 * p2) - p3) * t2)
            + (((3.0 * p1) - p0 - (3.0 * p2) + p3) * t3));
    }

    /// <summary>
    /// Surrounds a hi-res tile buffer with a <paramref name="pad"/>-pixel ring cut from its 8 neighbours'
    /// hi-res buffers, on the pixel-AREA convention of the WCS grids: adjacent tiles are CONTIGUOUS with no
    /// shared column (tile A's last column strip abuts tile B's column 0 — unlike the baked 256-node
    /// pixel-is-point grids, which share their boundary line). Where a neighbour is unavailable or mis-sized
    /// the ring keeps <paramref name="fill"/> (the NoData sentinel), which
    /// <see cref="LowPassDownsampleToNodes"/> excludes — degrading to a deterministic clamped window on that
    /// side.
    /// </summary>
    /// <param name="core">Row-major hi-res samples of the tile itself, length coreN².</param>
    /// <param name="coreN">The tile buffer's side in pixels.</param>
    /// <param name="pad">Ring width in pixels (≥ 0).</param>
    /// <param name="fill">Value for ring cells with no usable neighbour data (the NoData sentinel).</param>
    /// <param name="neighbour">Returns the (dx, dy) neighbour's coreN² hi-res buffer, or null when absent.
    /// Called at most once per neighbour; the buffer must be in the same units/sanitisation as the core.</param>
    /// <returns>A (coreN + 2·pad)² row-major buffer: the core centred in a neighbour-filled ring.</returns>
    public static float[] PadWithNeighbours(
        float[] core, int coreN, int pad, float fill, Func<int, int, float[]?> neighbour)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(neighbour);
        if (coreN <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(coreN), coreN, "Must be positive.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(pad);
        if (core.Length != coreN * coreN)
        {
            throw new ArgumentException($"Expected {coreN * coreN} samples, got {core.Length}.", nameof(core));
        }

        int n = coreN + (2 * pad);
        var padded = new float[n * n];
        Array.Fill(padded, fill);
        for (int r = 0; r < coreN; r++)
        {
            Array.Copy(core, r * coreN, padded, ((r + pad) * n) + pad, coreN);
        }

        if (pad == 0)
        {
            return padded;
        }

        var neighbours = new Dictionary<(int Dx, int Dy), float[]?>();
        float[]? At(int dx, int dy)
        {
            if (!neighbours.TryGetValue((dx, dy), out float[]? grid))
            {
                grid = neighbour(dx, dy);
                if (grid is not null && grid.Length != coreN * coreN)
                {
                    grid = null; // a mis-sized buffer cannot be aligned — treat as absent
                }

                neighbours[(dx, dy)] = grid;
            }

            return grid;
        }

        for (int pr = 0; pr < n; pr++)
        {
            int gr = pr - pad; // core-grid row; outside [0, coreN-1] in the ring
            bool rowInside = gr >= 0 && gr < coreN;
            for (int pc = 0; pc < n; pc++)
            {
                int gc = pc - pad;
                if (rowInside && gc >= 0 && gc < coreN)
                {
                    pc = pad + coreN - 1; // core span already copied — jump to the east ring band
                    continue;
                }

                // Pixel-area contiguity: the cell j past the core's east edge is the east neighbour's
                // column j — a shift of the FULL side, not side−1.
                int dx = gc < 0 ? -1 : gc >= coreN ? 1 : 0;
                int dy = gr < 0 ? -1 : gr >= coreN ? 1 : 0;
                if (At(dx, dy) is { } grid)
                {
                    int nc = gc - (dx * coreN);
                    int nr = gr - (dy * coreN);
                    padded[(pr * n) + pc] = grid[(nr * coreN) + nc];
                }
            }
        }

        return padded;
    }
}