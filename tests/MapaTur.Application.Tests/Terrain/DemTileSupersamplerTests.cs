using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class DemTileSupersamplerTests
{
    // GUGiK WCS resamples its 1 m grid to the requested WIDTH/HEIGHT server-side. At a coarse grid
    // (256 px over a ~5 km z13 tile) that resample bakes in a diagonal "washboard" ripple; requesting a
    // denser grid makes the server resample gently. SupersampleFactor decides how many times the base
    // grid to over-request so metres-per-pixel stays near the target, capped.

    [Fact]
    public void SupersampleFactor_CoarseTile_RequestsDenserGrid()
    {
        // z13 tile ≈ 4900 m of ground; at 256 px that is ~19 m/px (washboard). Target ~5 m/px → ×4.
        int factor = DemTileSupersampler.SupersampleFactor(
            tileGroundMeters: 4900, baseTileSize: 256, targetMetersPerPixel: 5, maxFactor: 4);

        factor.Should().Be(4);
    }

    [Fact]
    public void SupersampleFactor_FineTile_RequestsAsIs()
    {
        // z16 tile ≈ 150 m of ground; at 256 px that is already ~0.6 m/px (near native) → no over-request.
        int factor = DemTileSupersampler.SupersampleFactor(
            tileGroundMeters: 150, baseTileSize: 256, targetMetersPerPixel: 5, maxFactor: 4);

        factor.Should().Be(1);
    }

    [Fact]
    public void SupersampleFactor_HugeTile_ClampedToMax()
    {
        int factor = DemTileSupersampler.SupersampleFactor(
            tileGroundMeters: 100_000, baseTileSize: 256, targetMetersPerPixel: 5, maxFactor: 4);

        factor.Should().Be(4);
    }

    [Fact]
    public void AreaAverageDownsample_FactorTwo_AveragesEachBlock()
    {
        // 4×4 high-res grid (baseN=2, factor=2), row-major.
        float[] highRes =
        {
            1, 2, 3, 4,
            5, 6, 7, 8,
            9, 10, 11, 12,
            13, 14, 15, 16,
        };

        float[] result = DemTileSupersampler.AreaAverageDownsample(highRes, baseN: 2, factor: 2, noData: -32768f);

        // Block (0,0)={1,2,5,6}=3.5  (0,1)={3,4,7,8}=5.5  (1,0)={9,10,13,14}=11.5  (1,1)={11,12,15,16}=13.5
        var expected = new[] { 3.5f, 5.5f, 11.5f, 13.5f };
        result.Should().Equal(expected);
    }

    [Fact]
    public void AreaAverageDownsample_ExcludesNoDataFromBlockMean()
    {
        // Single 2×2 block (baseN=1, factor=2) with one NoData sample → mean of the three valid ones.
        float[] highRes = { 10f, -32768f, 20f, 30f };

        float[] result = DemTileSupersampler.AreaAverageDownsample(highRes, baseN: 1, factor: 2, noData: -32768f);

        result.Should().HaveCount(1);
        result[0].Should().BeApproximately(20f, 0.001f); // (10+20+30)/3
    }

    [Fact]
    public void AreaAverageDownsample_AllNoDataBlock_StaysNoData()
    {
        float[] highRes = { -32768f, -32768f, -32768f, -32768f };

        float[] result = DemTileSupersampler.AreaAverageDownsample(highRes, baseN: 1, factor: 2, noData: -32768f);

        result[0].Should().Be(-32768f);
    }

    [Fact]
    public void AreaAverageDownsample_FactorOne_ReturnsCopyOfInput()
    {
        float[] highRes = { 1f, 2f, 3f, 4f };

        float[] result = DemTileSupersampler.AreaAverageDownsample(highRes, baseN: 2, factor: 1, noData: -32768f);

        result.Should().Equal(highRes);
    }

    // LowPassDownsample replaces the box average: a Gaussian-weighted, OVERLAPPING window that removes the
    // moiré ring-grid the box left (disjoint blocks + leaky low-pass → aliased WCS ripple = "obwódki").

    [Fact]
    public void LowPassDownsample_ConstantGrid_ReturnsConstant()
    {
        // Flat terrain must stay flat (no distortion), edges included.
        var highRes = new float[8 * 8];
        Array.Fill(highRes, 100f);

        float[] result = DemTileSupersampler.LowPassDownsample(highRes, baseN: 4, factor: 2, noData: -32768f);

        result.Should().HaveCount(16);
        foreach (float v in result)
        {
            v.Should().BeApproximately(100f, 0.001f);
        }
    }

    // ── Neighbour-padded downsample ─────────────────────────────────────────────────────────────────────
    // The Gaussian window CLAMPS at the tile edge (no neighbour data), which shifts the outer row/column's
    // sampling centroid ~0.85 hi-px INTO the tile. On a slope that is a real height kink at every tile
    // border — measured on the baked z17 pyramid as p95 |curvature residual| ≈ 1.0 m at ±1 cell from borders
    // vs 0.44 m terrain background (Morskie Oko cirque). The pad overload lets the caller supply a ring of
    // the NEIGHBOURS' hi-res pixels so border output cells see exactly what an interior cell sees.

    private const float Sentinel = -32768f;

    // One analytic hi-res field sampled on a GLOBAL grid, so strips cut from "neighbour tiles" agree with
    // the big borderless buffer by construction. Curved + cross term: a pure ramp would hide the centroid
    // shift (symmetric window on a linear field averages back to the centre).
    private static float Field(int gc, int gr) => 1000f + (0.5f * gc * gc) + (0.3f * gr * gr) + (0.2f * gc * gr);

    // The hi-res grid of the tile at tile-coords (tx, ty), tileN samples per side, cut from Field on the
    // pixel-AREA convention: tile (tx,ty) owns global columns [tx·tileN .. tx·tileN+tileN−1] — adjacent
    // tiles are contiguous with NO shared column (unlike the baked 256-node pixel-is-point grids).
    private static float[] HiResTile(int tx, int ty, int tileN)
    {
        var tile = new float[tileN * tileN];
        for (int r = 0; r < tileN; r++)
        {
            for (int c = 0; c < tileN; c++)
            {
                tile[(r * tileN) + c] = Field((tx * tileN) + c, (ty * tileN) + r);
            }
        }

        return tile;
    }

    // The NODE-REGISTERED downsample (the §1.3 registration fix, 2026-07-15 evening). The block-centre
    // mapping put output sample j at hi position 2j+0.5, but the pipeline READS output j as the NODE at
    // ground j/(baseN−1) — hi position j·highN/(baseN−1) − 0.5. The mismatch squeezed every tile's content
    // ~half a hi-px toward its centre, so adjacent tiles' content SEPARATED by ~one hi-px at every border
    // and the weld bridged the gap — a slope-proportional crease line along every tile border (the residual
    // the border gate caught after the clamp fix: ±1 p95 0.48 m vs 0.28 m control). Sampling AT the node
    // positions removes the squeeze, and the border node of two adjacent tiles reads the IDENTICAL global
    // window — bit-identical by construction, so the weld becomes a no-op.

    [Fact]
    public void LowPassDownsampleToNodes_OnAGlobalPlane_ReturnsTheExactNodeValues()
    {
        // A plane is the registration litmus: any residual sampling-position error shows up as a value
        // error proportional to the slope, anywhere in the tile — not just at borders.
        const int tileN = 16;
        const int baseN = 8;
        int pad = DemTileSupersampler.LowPassKernelRadius(2) + 1;
        static float Plane(int gc, int gr) => 500f + (0.75f * gc) + (0.25f * gr);

        var core = new float[tileN * tileN];
        for (int r = 0; r < tileN; r++)
        {
            for (int c = 0; c < tileN; c++)
            {
                core[(r * tileN) + c] = Plane((1 * tileN) + c, (1 * tileN) + r);
            }
        }

        float[] padded = DemTileSupersampler.PadWithNeighbours(
            core, tileN, pad, Sentinel, (dx, dy) =>
            {
                var n = new float[tileN * tileN];
                for (int r = 0; r < tileN; r++)
                {
                    for (int c = 0; c < tileN; c++)
                    {
                        n[(r * tileN) + c] = Plane(((1 + dx) * tileN) + c, ((1 + dy) * tileN) + r);
                    }
                }

                return n;
            });

        float[] nodes = DemTileSupersampler.LowPassDownsampleToNodes(padded, baseN, factor: 2, noData: Sentinel, pad);

        for (int j = 0; j < baseN; j++)
        {
            for (int i = 0; i < baseN; i++)
            {
                // Node (i, j) of tile (1,1) sits at global hi position tileN + i·tileN/(baseN−1) − 0.5.
                double gx = tileN + (i * (double)tileN / (baseN - 1)) - 0.5;
                double gy = tileN + (j * (double)tileN / (baseN - 1)) - 0.5;
                double expected = 500.0 + (0.75 * gx) + (0.25 * gy);
                // Tolerance: a windowed resampler's integer support is slightly asymmetric at general
                // fractional centres — a ~cm interpolation bias even at this test's cliff-grade slope
                // (0.75 m/px). That is uniform, not border-concentrated, and orders below the ~1 hi-px × slope
                // registration error the old block-centre mapping shows here (≈0.75 m at the edge nodes) —
                // which is what keeps this test RED against the squeeze. Cross-tile bit-identity of the shared
                // border node (the seam invariant) is pinned by its own dedicated test below.
                nodes[(j * baseN) + i].Should().BeApproximately(
                    (float)expected, 0.02f,
                    $"node ({i},{j}) must sit ON the node lattice, not squeezed off it");
            }
        }
    }

    [Fact]
    public void LowPassDownsampleToNodes_AdjacentTiles_ComputeTheSharedBorderNodeBitIdentically()
    {
        // The border node of tile A (its last column) and of tile B (its column 0) is ONE world position:
        // both must read the same global window through their own padded buffers and produce the identical
        // float — which is what turns the baker's weld into a no-op instead of a gap-bridging average.
        const int tileN = 16;
        const int baseN = 8;
        int pad = DemTileSupersampler.LowPassKernelRadius(2) + 1;

        float[] a = DemTileSupersampler.PadWithNeighbours(
            HiResTile(1, 1, tileN), tileN, pad, Sentinel, (dx, dy) => HiResTile(1 + dx, 1 + dy, tileN));
        float[] b = DemTileSupersampler.PadWithNeighbours(
            HiResTile(2, 1, tileN), tileN, pad, Sentinel, (dx, dy) => HiResTile(2 + dx, 1 + dy, tileN));

        float[] aNodes = DemTileSupersampler.LowPassDownsampleToNodes(a, baseN, 2, Sentinel, pad);
        float[] bNodes = DemTileSupersampler.LowPassDownsampleToNodes(b, baseN, 2, Sentinel, pad);

        for (int r = 0; r < baseN; r++)
        {
            aNodes[(r * baseN) + baseN - 1].Should().Be(
                bNodes[r * baseN], $"row {r}: one world position, one value — regardless of which tile computed it");
        }
    }

    [Fact]
    public void LowPassDownsampleToNodes_WithAllSentinelPadding_IsDeterministicAndFinite()
    {
        // The pyramid rim / missing-neighbour fallback: the window clamps to whatever real samples exist and
        // renormalises. Heights near the rim legitimately move relative to the old block-centre output (the
        // registration change is global by design — this is a bake-time path, applied by a full re-bake);
        // the contract here is determinism and no sentinel leakage, pinned against the wired-in reference.
        const int tileN = 16;
        const int baseN = 8;
        int pad = DemTileSupersampler.LowPassKernelRadius(2) + 1;

        float[] core = HiResTile(0, 0, tileN);
        float[] paddedBuffer = DemTileSupersampler.PadWithNeighbours(core, tileN, pad, Sentinel, (_, _) => null);

        float[] first = DemTileSupersampler.LowPassDownsampleToNodes(paddedBuffer, baseN, 2, Sentinel, pad);
        float[] second = DemTileSupersampler.LowPassDownsampleToNodes(paddedBuffer, baseN, 2, Sentinel, pad);

        first.Should().Equal(second);
        first.Should().OnlyContain(v => v != Sentinel && !float.IsNaN(v));
    }

    [Fact]
    public void LowPassDownsampleToNodes_PadBelowTheKernelReach_Throws()
    {
        // A node at the tile edge samples half a hi-px OUTSIDE the tile; the window needs radius+1 of
        // padding or it silently clamps and re-introduces the squeeze. Fail loudly on a mis-sized pad.
        const int tileN = 16;
        int pad = DemTileSupersampler.LowPassKernelRadius(2); // one short of the required radius+1

        float[] padded = DemTileSupersampler.PadWithNeighbours(
            HiResTile(0, 0, tileN), tileN, pad, Sentinel, (_, _) => null);

        FluentActions.Invoking(() => DemTileSupersampler.LowPassDownsampleToNodes(padded, 8, 2, Sentinel, pad))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    // ── Node-registered NATIVE resample (z16 + legacy DMR5 tiles, 2026-07-15 late) ─────────────────────
    // Native-size fetches (256 px, factor 1 — z16, and the injected legacy z17/z16 DMR5 tiles) carry the SAME
    // registration flaw as the z17 supersample did, at twice the magnitude: the WCS/DMR5 grid is pixel-centre
    // over the tile bbox (verified on real tifs: |A[edge]−B[0]| ≈ one-cell step, so grids are contiguous),
    // but the whole pipeline reads sample j as the NODE at ground j/(N−1). Measured on the baked pyramid:
    // z16 borders ±1 p95 = 1.69 m vs 1.17 m control; SK↔SK legacy z17 medians ±4.3 cm. The fix is the same
    // node registration, with Catmull-Rom instead of a Gaussian — native data must not be blurred.

    [Fact]
    public void ResampleToNodes_OnAGlobalPlane_ReturnsTheExactNodeValues()
    {
        const int tileN = 16;
        int pad = DemTileSupersampler.NativeNodePadCells;
        static float Plane(int gc, int gr) => 700f + (0.6f * gc) + (0.2f * gr);

        var core = new float[tileN * tileN];
        for (int r = 0; r < tileN; r++)
        {
            for (int c = 0; c < tileN; c++)
            {
                core[(r * tileN) + c] = Plane(tileN + c, tileN + r);
            }
        }

        float[] padded = DemTileSupersampler.PadWithNeighbours(
            core, tileN, pad, Sentinel, (dx, dy) =>
            {
                var n = new float[tileN * tileN];
                for (int r = 0; r < tileN; r++)
                {
                    for (int c = 0; c < tileN; c++)
                    {
                        n[(r * tileN) + c] = Plane(((1 + dx) * tileN) + c, ((1 + dy) * tileN) + r);
                    }
                }

                return n;
            });

        float[] nodes = DemTileSupersampler.ResampleToNodes(padded, tileN, Sentinel, pad);

        for (int j = 0; j < tileN; j++)
        {
            for (int i = 0; i < tileN; i++)
            {
                // Node (i, j) sits at pixel-centre-lattice position i·tileN/(tileN−1) − 0.5 within the tile.
                double gx = tileN + (i * (double)tileN / (tileN - 1)) - 0.5;
                double gy = tileN + (j * (double)tileN / (tileN - 1)) - 0.5;
                double expected = 700.0 + (0.6 * gx) + (0.2 * gy);
                nodes[(j * tileN) + i].Should().BeApproximately(
                    (float)expected, 0.001f,
                    $"node ({i},{j}): Catmull-Rom has exact linear precision, so a plane pins the registration");
            }
        }
    }

    [Fact]
    public void ResampleToNodes_AdjacentTiles_ComputeTheSharedBorderNodeBitIdentically()
    {
        const int tileN = 16;
        int pad = DemTileSupersampler.NativeNodePadCells;

        float[] a = DemTileSupersampler.PadWithNeighbours(
            HiResTile(1, 1, tileN), tileN, pad, Sentinel, (dx, dy) => HiResTile(1 + dx, 1 + dy, tileN));
        float[] b = DemTileSupersampler.PadWithNeighbours(
            HiResTile(2, 1, tileN), tileN, pad, Sentinel, (dx, dy) => HiResTile(2 + dx, 1 + dy, tileN));

        float[] aNodes = DemTileSupersampler.ResampleToNodes(a, tileN, Sentinel, pad);
        float[] bNodes = DemTileSupersampler.ResampleToNodes(b, tileN, Sentinel, pad);

        for (int r = 0; r < tileN; r++)
        {
            aNodes[(r * tileN) + tileN - 1].Should().Be(
                bNodes[r * tileN], $"row {r}: the shared border node reads the same global 4×4 window from both sides");
        }
    }

    [Fact]
    public void ResampleToNodes_VoidsPropagate_AndDoNotSmearIntoValidGround()
    {
        const int tileN = 16;
        int pad = DemTileSupersampler.NativeNodePadCells;
        var core = new float[tileN * tileN];
        Array.Fill(core, 1200f);
        for (int r = 6; r < 10; r++)
        {
            for (int c = 6; c < 10; c++)
            {
                core[(r * tileN) + c] = Sentinel; // an interior void block
            }
        }

        float[] padded = DemTileSupersampler.PadWithNeighbours(core, tileN, pad, Sentinel, (_, _) => null);
        float[] nodes = DemTileSupersampler.ResampleToNodes(padded, tileN, Sentinel, pad);

        nodes[(8 * tileN) + 8].Should().Be(Sentinel, "an all-void footprint stays a hole — never fabricated");
        nodes[(2 * tileN) + 2].Should().BeApproximately(1200f, 0.001f, "far ground is untouched");
        nodes[(6 * tileN) + 4].Should().BeApproximately(
            1200f, 0.001f, "a footprint touching the void degrades to the valid taps — no sentinel bleed");
    }

    [Fact]
    public void ResampleToNodes_PadBelowTheTapReach_Throws()
    {
        const int tileN = 16;
        var padded = new float[(tileN + 2) * (tileN + 2)]; // pad 1 < required 2

        FluentActions.Invoking(() => DemTileSupersampler.ResampleToNodes(padded, tileN, Sentinel, pad: 1))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void UpsamplePixelCentreGrid_OnAGlobalPlane_ProducesTheExactFinerLattice()
    {
        // The PL↔SK apron: a legacy 256 px neighbour has no hi-res tif, so its pixel-centre grid is CR-upsampled
        // onto the 512 pixel-centre lattice to feed the z17 padded kernel. Same-plane exactness pins alignment:
        // fine pixel i sits at coarse-lattice position (i+0.5)/2 − 0.5.
        const int srcN = 16;
        const int dstN = 32;
        var src = new float[srcN * srcN];
        for (int r = 0; r < srcN; r++)
        {
            for (int c = 0; c < srcN; c++)
            {
                src[(r * srcN) + c] = 300f + (2f * c) + (1f * r);
            }
        }

        float[] fine = DemTileSupersampler.UpsamplePixelCentreGrid(src, srcN, dstN, Sentinel);

        fine.Should().HaveCount(dstN * dstN);
        for (int i = 4; i < dstN - 4; i += 5)
        {
            double u = ((i + 0.5) / 2.0) - 0.5;
            fine[((dstN / 2) * dstN) + i].Should().BeApproximately(
                (float)(300.0 + (2.0 * u) + (((dstN / 2) + 0.5) / 2.0 - 0.5) * 1.0), 0.001f,
                $"fine column {i} must sit exactly on the coarse lattice mapping");
        }
    }

    [Fact]
    public void PadWithNeighbours_PlacesEveryStripOnTheContiguousGlobalGrid()
    {
        // Each neighbour marked with a distinct constant; the pad ring must read the RIGHT strip of the
        // RIGHT neighbour on pixel-area contiguity: the cell just past the core's east edge is the east
        // neighbour's column 0; just past north is the north neighbour's LAST row.
        const int tileN = 8;
        const int pad = 2;
        var core = new float[tileN * tileN];
        Array.Fill(core, 5f);
        static float[] Marked(float v)
        {
            var g = new float[8 * 8];
            Array.Fill(g, v);
            return g;
        }

        float[] padded = DemTileSupersampler.PadWithNeighbours(
            core, tileN, pad, Sentinel,
            (dx, dy) => Marked(100f + (dx * 10f) + dy));

        int n = tileN + (2 * pad);
        padded.Should().HaveCount(n * n);
        padded[(pad * n) + pad].Should().Be(5f);                          // core NW
        padded[((pad + tileN - 1) * n) + pad + tileN - 1].Should().Be(5f); // core SE
        padded[((pad + 3) * n) + pad + tileN].Should().Be(110f);          // east strip (dx=+1, dy=0)
        padded[((pad + 3) * n) + pad - 1].Should().Be(90f);               // west strip (dx=−1)
        padded[((pad - 1) * n) + pad + 3].Should().Be(99f);               // north strip (dy=−1)
        padded[((pad + tileN) * n) + pad + 3].Should().Be(101f);          // south strip (dy=+1)
        padded[0].Should().Be(89f);                                       // NW corner (dx=−1, dy=−1)
        padded[(n * n) - 1].Should().Be(111f);                            // SE corner (dx=+1, dy=+1)
    }

    [Fact]
    public void PadWithNeighbours_MissingOrMisSizedNeighbour_LeavesTheSentinel()
    {
        const int tileN = 8;
        const int pad = 2;
        var core = new float[tileN * tileN];
        Array.Fill(core, 5f);

        float[] padded = DemTileSupersampler.PadWithNeighbours(
            core, tileN, pad, Sentinel,
            (dx, dy) => dx == 1 && dy == 0 ? new float[3] : null); // east exists but wrong size → unusable

        int n = tileN + (2 * pad);
        padded[(pad * n) + pad + tileN].Should().Be(Sentinel, "a mis-sized neighbour must not be misread");
        padded[(pad * n) + pad - 1].Should().Be(Sentinel, "a missing neighbour leaves the sentinel (clamp fallback)");
        padded[(pad * n) + pad].Should().Be(5f, "the core is untouched");
    }

    [Fact]
    public void LowPassDownsample_FactorOne_ReturnsCopyOfInput()
    {
        float[] highRes = { 1f, 2f, 3f, 4f };

        float[] result = DemTileSupersampler.LowPassDownsample(highRes, baseN: 2, factor: 1, noData: -32768f);

        result.Should().Equal(highRes);
    }

    [Fact]
    public void LowPassDownsample_AllNoDataNeighbourhood_StaysNoData()
    {
        var highRes = new float[4 * 4];
        Array.Fill(highRes, -32768f);

        float[] result = DemTileSupersampler.LowPassDownsample(highRes, baseN: 2, factor: 2, noData: -32768f);

        result.Should().OnlyContain(v => v == -32768f);
    }

    [Fact]
    public void LowPassDownsample_OverlapsBlocks_SpreadingAcrossBoundaries_UnlikeBox()
    {
        // A single high-res spike. The box average confines it to its one block; the Gaussian window overlaps
        // neighbouring blocks, so the spike's energy bleeds into an ADJACENT output cell — that overlap is what
        // dissolves the block-boundary moiré. baseN=3, factor=2 → highN=6; spike at (row 3, col 3).
        var highRes = new float[6 * 6];
        highRes[(3 * 6) + 3] = 100f;

        float[] box = DemTileSupersampler.AreaAverageDownsample(highRes, baseN: 3, factor: 2, noData: -32768f);
        float[] low = DemTileSupersampler.LowPassDownsample(highRes, baseN: 3, factor: 2, noData: -32768f);

        // Output cell (row 2, col 1): the box keeps it 0 (different block); the Gaussian leaks the spike into it.
        int adjacent = (2 * 3) + 1;
        box[adjacent].Should().Be(0f);
        low[adjacent].Should().BeGreaterThan(0f);
    }
}