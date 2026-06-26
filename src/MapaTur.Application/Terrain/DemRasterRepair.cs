using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Repairs gaps in a <see cref="DemRaster"/> before it reaches the mesh builder, which has no NoData
/// handling (a NoData sample would otherwise become a vertex at the sentinel depth — a spike/streak).
/// <see cref="FillNoData"/> replaces every NoData cell with the nearest valid elevation along its row,
/// then its column, so a region whose bbox clips a coverage edge (e.g. the Slovak border for a GUGiK
/// Tatra patch) extends flat to the edge instead of plunging.
/// </summary>
public static class DemRasterRepair
{
    /// <summary>
    /// Returns a copy of <paramref name="raster"/> with NoData cells filled from the nearest valid
    /// neighbour (row pass, then column pass). A raster with no valid samples is returned unchanged.
    /// </summary>
    public static DemRaster FillNoData(DemRaster raster)
    {
        ArgumentNullException.ThrowIfNull(raster);

        float noData = raster.NoDataValue;
        int cols = raster.Columns;
        int rows = raster.Rows;
        float[] s = (float[])raster.Samples.Clone();

        bool IsHole(float v) => v.Equals(noData) || float.IsNaN(v);

        // Row pass: forward then backward fill within each row.
        for (int r = 0; r < rows; r++)
        {
            int rowBase = r * cols;
            FillRun(s, rowBase, cols, stride: 1, IsHole);
            FillRun(s, rowBase + ((cols - 1) * 1), cols, stride: -1, IsHole);
        }

        // Column pass: fills cells the row pass couldn't (whole-NoData rows), top-down then bottom-up.
        for (int c = 0; c < cols; c++)
        {
            FillRun(s, c, rows, stride: cols, IsHole);
            FillRun(s, c + ((rows - 1) * cols), rows, stride: -cols, IsHole);
        }

        return new DemRaster(cols, rows, raster.Bounds, s, noData);
    }

    /// <summary>
    /// Returns a copy of <paramref name="target"/> with each NoData cell filled from <paramref name="source"/>'s
    /// bilinear elevation at that cell's geographic position. GUGiK NMT has NoData voids ALONG WATERCOURSES
    /// (no LiDAR ground return on water) and past the Polish border; the NoData-aware mesh DROPS triangles
    /// there, which reads as chains of black see-through slits along streams. Backfilling from the coarse base
    /// renders base-height terrain in the voids instead — same visual as the base, no slits. A cell where the
    /// source is ALSO NoData stays NoData (the mesh still holes it honestly).
    /// </summary>
    public static DemRaster FillNoDataFrom(DemRaster target, DemRaster source)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);

        float noData = target.NoDataValue;
        int cols = target.Columns;
        int rows = target.Rows;
        float[] s = (float[])target.Samples.Clone();

        bool IsHole(float v) => v.Equals(noData) || float.IsNaN(v);

        double west = target.West;
        double east = target.East;
        double north = target.North;
        double south = target.South;

        for (int r = 0; r < rows; r++)
        {
            double lat = rows > 1 ? north - ((double)r / (rows - 1) * (north - south)) : north;
            for (int c = 0; c < cols; c++)
            {
                int i = (r * cols) + c;
                if (!IsHole(s[i]))
                {
                    continue;
                }

                double lon = cols > 1 ? west + ((double)c / (cols - 1) * (east - west)) : west;
                double v = source.SampleBilinear(lon, lat);
                if (!v.Equals((double)source.NoDataValue) && !double.IsNaN(v))
                {
                    s[i] = (float)v;
                }
            }
        }

        return new DemRaster(cols, rows, target.Bounds, s, noData);
    }

    /// <summary>
    /// <see cref="FillNoDataFrom"/> applied IN PLACE — mutates <paramref name="target"/>'s samples instead of
    /// cloning a fresh ~window-sized array. Use only when the caller owns <paramref name="target"/>; avoids a
    /// ~90 MB Large Object Heap allocation per detail build.
    /// </summary>
    public static void FillNoDataFromInPlace(DemRaster target, DemRaster source)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);

        float noData = target.NoDataValue;
        int cols = target.Columns;
        int rows = target.Rows;
        float[] s = target.Samples;

        bool IsHole(float v) => v.Equals(noData) || float.IsNaN(v);

        double west = target.West;
        double east = target.East;
        double north = target.North;
        double south = target.South;

        for (int r = 0; r < rows; r++)
        {
            double lat = rows > 1 ? north - ((double)r / (rows - 1) * (north - south)) : north;
            for (int c = 0; c < cols; c++)
            {
                int i = (r * cols) + c;
                if (!IsHole(s[i]))
                {
                    continue;
                }

                double lon = cols > 1 ? west + ((double)c / (cols - 1) * (east - west)) : west;
                double v = source.SampleBilinear(lon, lat);
                if (!v.Equals((double)source.NoDataValue) && !double.IsNaN(v))
                {
                    s[i] = (float)v;
                }
            }
        }
    }

    /// <summary>
    /// Returns a copy with single-cell PITS raised to their neighbour minimum: a valid cell more than
    /// <paramref name="depthThresholdMeters"/> below the MIN of its valid 4-neighbours is a data artefact
    /// (tatry.dem carries one-cell shafts hundreds of metres deep at regular processing-grid positions —
    /// water/void artefacts of the bake), which renders as a cell-wide dark-walled trench (the black
    /// "dashes" along valleys). Real terrain never drops a single ~15 m cell by 20+ m below all four sides.
    /// NoData cells are left alone and excluded from neighbour minima.
    /// </summary>
    public static DemRaster FillPits(DemRaster raster, double depthThresholdMeters)
    {
        ArgumentNullException.ThrowIfNull(raster);

        int cols = raster.Columns;
        int rows = raster.Rows;
        float noData = raster.NoDataValue;
        float[] src = (float[])raster.Samples.Clone();

        bool IsHole(float v) => v.Equals(noData) || float.IsNaN(v);

        // Criterion + fill value = the MEDIAN of the valid 4-neighbours. A trench cell's neighbours are
        // [low(along), low(along), high(wall), high(wall)] → median ≈ halfway up the wall → the trench depth
        // HALVES every pass (converges in ~log₂(depth/threshold) passes regardless of trench length, unlike
        // end-erosion). A genuine V-valley bottom ([slightly-down, slightly-up, wall, wall]) keeps its median
        // within the threshold, so real terrain is untouched. Verified offline on tatry.dem: 6.5 k cells
        // changed (0.07% — the artefact trench network along watercourses), 0 residual pits.
        const int MaxPasses = 12;
        Span<float> nb = stackalloc float[4];
        for (int pass = 0; pass < MaxPasses; pass++)
        {
            float[] dst = (float[])src.Clone();
            bool changed = false;
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    int i = (r * cols) + c;
                    float v = src[i];
                    if (IsHole(v))
                    {
                        continue;
                    }

                    int n = 0;
                    if (c > 0 && !IsHole(src[i - 1])) { nb[n++] = src[i - 1]; }
                    if (c < cols - 1 && !IsHole(src[i + 1])) { nb[n++] = src[i + 1]; }
                    if (r > 0 && !IsHole(src[i - cols])) { nb[n++] = src[i - cols]; }
                    if (r < rows - 1 && !IsHole(src[i + cols])) { nb[n++] = src[i + cols]; }
                    if (n == 0)
                    {
                        continue;
                    }

                    Span<float> s = nb[..n];
                    s.Sort();
                    // Median: 4 valid → mean of the middle two; 3 → the middle; 2 → the higher; 1 → the one.
                    float reference = n switch
                    {
                        4 => (s[1] + s[2]) * 0.5f,
                        3 => s[1],
                        2 => s[1],
                        _ => s[0],
                    };

                    if (reference - v > depthThresholdMeters)
                    {
                        dst[i] = reference;
                        changed = true;
                    }
                }
            }

            src = dst;
            if (!changed)
            {
                break;
            }
        }

        return new DemRaster(cols, rows, raster.Bounds, src, noData);
    }

    /// <summary>
    /// Returns a copy with every valid cell strictly below <paramref name="floorMeters"/> set to NoData, so
    /// the NoData-aware mesh holes them through to the base. GUGiK returns a flat ~0 OUTSIDE its coverage
    /// (instead of a NoData sentinel), which would otherwise render as a flat plate below the terrain; for a
    /// mountain patch (real terrain well above the floor) this drops only those coverage-edge artefacts.
    /// Note: a Tatra-context guard — a per-cell floor would hole genuine lowland if used nationwide.
    /// </summary>
    public static DemRaster HoleBelow(DemRaster raster, double floorMeters)
    {
        ArgumentNullException.ThrowIfNull(raster);

        float noData = raster.NoDataValue;
        float[] s = (float[])raster.Samples.Clone();
        for (int i = 0; i < s.Length; i++)
        {
            if (!s[i].Equals(noData) && s[i] < floorMeters)
            {
                s[i] = noData;
            }
        }

        return new DemRaster(raster.Columns, raster.Rows, raster.Bounds, s, noData);
    }

    /// <summary>
    /// <see cref="HoleBelow"/> applied IN PLACE — mutates <paramref name="raster"/>'s samples instead of cloning a
    /// fresh ~window-sized array. Use only when the caller owns the raster (e.g. a freshly-built detail window);
    /// avoids a ~90 MB Large Object Heap allocation per detail build (a source of the GC stalls during a flight).
    /// </summary>
    public static void HoleBelowInPlace(DemRaster raster, double floorMeters)
    {
        ArgumentNullException.ThrowIfNull(raster);

        float noData = raster.NoDataValue;
        float[] s = raster.Samples;
        for (int i = 0; i < s.Length; i++)
        {
            if (!s[i].Equals(noData) && s[i] < floorMeters)
            {
                s[i] = noData;
            }
        }
    }

    /// <summary>
    /// Fills only INTERIOR gaps, keeping gaps connected to the raster edge as holes. The base's bbox spills
    /// past GUGiK/Terrarium coverage, leaving a large edge-connected no-data region that must stay a hole
    /// (→ sky, the honest finite-terrain edge) rather than a flat plate — while small interior gaps are
    /// filled so the base shows no see-through white windows. Used for the LOD base (a bottom layer with no
    /// further fallback); the detail patch holes ALL its gaps instead, so the base shows through.
    /// </summary>
    public static DemRaster FillInteriorKeepEdgeGaps(DemRaster raster)
    {
        ArgumentNullException.ThrowIfNull(raster);

        int cols = raster.Columns;
        int rows = raster.Rows;
        float noData = raster.NoDataValue;
        float[] src = raster.Samples;

        bool IsHole(float v) => v.Equals(noData) || float.IsNaN(v);

        // Flood-fill (4-connectivity) from every boundary hole to mark the edge-connected out-of-coverage.
        var edgeConnected = new bool[cols * rows];
        var queue = new Queue<int>();
        void Seed(int c, int r)
        {
            int i = (r * cols) + c;
            if (IsHole(src[i]) && !edgeConnected[i])
            {
                edgeConnected[i] = true;
                queue.Enqueue(i);
            }
        }

        for (int c = 0; c < cols; c++)
        {
            Seed(c, 0);
            Seed(c, rows - 1);
        }

        for (int r = 0; r < rows; r++)
        {
            Seed(0, r);
            Seed(cols - 1, r);
        }

        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            int c = i % cols;
            int r = i / cols;
            if (c > 0)
            {
                Seed(c - 1, r);
            }

            if (c < cols - 1)
            {
                Seed(c + 1, r);
            }

            if (r > 0)
            {
                Seed(c, r - 1);
            }

            if (r < rows - 1)
            {
                Seed(c, r + 1);
            }
        }

        // Fill every gap, then re-open only the edge-connected ones — interior gaps stay filled (no window).
        DemRaster filled = FillNoData(raster);
        float[] s = (float[])filled.Samples.Clone();
        for (int i = 0; i < s.Length; i++)
        {
            if (edgeConnected[i])
            {
                s[i] = noData;
            }
        }

        return new DemRaster(cols, rows, raster.Bounds, s, noData);
    }

    /// <summary>
    /// Bridges NARROW flat-zero strips from the surrounding valid 1 m data. GUGiK NMT z16 tiles sometimes
    /// come back with a flat-<c>0</c> strip along an edge (a thin coverage/processing dropout); because 0 m
    /// is a valid Polish elevation it survives the NoData filter and renders as a thin, dead-straight,
    /// deep-walled "fault" cutting across the terrain. This linearly interpolates each run of consecutive
    /// 0-cells that is (a) no wider than <paramref name="maxWidthCells"/> AND (b) bracketed by valid
    /// (non-zero, non-NoData) cells on BOTH sides — a true interior gap — first along rows (catching the
    /// vertical tile-edge strip), then along columns. WIDE 0-voids (whole-tile GUGiK holes, e.g. over a
    /// lake) and edge-touching (one-sided) runs are left as 0, so the downstream <see cref="HoleBelow"/> +
    /// base-backfill renders the real coarse base there instead of a fabricated smear across the gap.
    /// </summary>
    /// <param name="raster">DEM whose flat-0 dropouts should be bridged.</param>
    /// <param name="maxWidthCells">Widest 0-run (in cells) that is treated as a thin strip and interpolated.</param>
    public static DemRaster FillNarrowZeroStrips(DemRaster raster, int maxWidthCells)
    {
        ArgumentNullException.ThrowIfNull(raster);

        int cols = raster.Columns;
        int rows = raster.Rows;
        float noData = raster.NoDataValue;
        float[] s = (float[])raster.Samples.Clone();

        bool IsGap(float v) => v == 0f;
        bool IsValid(float v) => v != 0f && !v.Equals(noData) && !float.IsNaN(v);

        void BridgeLine(int start, int count, int stride)
        {
            int i = 0;
            while (i < count)
            {
                if (!IsGap(s[start + (i * stride)]))
                {
                    i++;
                    continue;
                }

                int j = i;
                while (j < count && IsGap(s[start + (j * stride)]))
                {
                    j++;
                }

                // Run of gaps is [i, j-1]; bracket cells are i-1 and j.
                bool bracketed = i > 0 && j < count
                    && IsValid(s[start + ((i - 1) * stride)])
                    && IsValid(s[start + (j * stride)]);
                if (bracketed && (j - i) <= maxWidthCells)
                {
                    float v0 = s[start + ((i - 1) * stride)];
                    float v1 = s[start + (j * stride)];
                    float span = j - (i - 1);
                    for (int k = i; k < j; k++)
                    {
                        float t = (k - (i - 1)) / span;
                        s[start + (k * stride)] = v0 + ((v1 - v0) * t);
                    }
                }

                i = j;
            }
        }

        // Row pass bridges vertical strips (each row crosses the strip as a short gap run); column pass
        // then bridges any horizontal strips left over.
        for (int r = 0; r < rows; r++)
        {
            BridgeLine(r * cols, cols, stride: 1);
        }

        for (int c = 0; c < cols; c++)
        {
            BridgeLine(c, rows, stride: cols);
        }

        return new DemRaster(cols, rows, raster.Bounds, s, noData);
    }

    /// <summary>
    /// Repairs corrupt ROW and COLUMN dropout STRIPS — a run of cells sitting far below BOTH of its
    /// perpendicular neighbours (a DEM mosaic/stitch artefact: a lidar scanline or LiDAR↔Copernicus seam that
    /// dropped a strip by hundreds of metres). Unlike <see cref="FillPits"/> (single cells; and it only
    /// converges to within its own threshold so it leaves a narrow RESIDUAL trench), this finds a horizontal
    /// (resp. vertical) RUN of at least <paramref name="minRunCells"/> consecutive cells each more than
    /// <paramref name="depthThresholdMeters"/> below the line on both sides, and replaces every cell in the run
    /// with the mean of the two bracketing lines — fully flattening the strip in one pass. The run requirement
    /// is what separates a systematic strip (a dead-straight cut) from an isolated pit or a real V-valley bottom
    /// (left untouched for <see cref="FillPits"/>). Rows first, then columns; both read a pre-pass snapshot so a
    /// just-filled cell never corrupts a neighbour's bracket.
    /// </summary>
    public static DemRaster FillDropoutStrips(DemRaster raster, double depthThresholdMeters = 50.0, int minRunCells = 3)
    {
        ArgumentNullException.ThrowIfNull(raster);

        int cols = raster.Columns;
        int rows = raster.Rows;
        float noData = raster.NoDataValue;
        float[] s = (float[])raster.Samples.Clone();

        bool IsValid(float v) => !v.Equals(noData) && !float.IsNaN(v);

        // ROW strips: a cell is a candidate if it sits > threshold below the rows ABOVE and BELOW it. Fill runs
        // of >= minRunCells consecutive candidates with the mean of the bracketing rows. Snapshot so the fill
        // reads original (un-filled) bracket values.
        float[] snap = (float[])s.Clone();
        bool RowCandidate(int r, int c)
        {
            float v = snap[(r * cols) + c], up = snap[((r - 1) * cols) + c], dn = snap[((r + 1) * cols) + c];
            return IsValid(v) && IsValid(up) && IsValid(dn) && Math.Min(up, dn) - v > depthThresholdMeters;
        }

        for (int r = 1; r < rows - 1; r++)
        {
            int c = 0;
            while (c < cols)
            {
                if (!RowCandidate(r, c))
                {
                    c++;
                    continue;
                }

                int j = c;
                while (j < cols && RowCandidate(r, j))
                {
                    j++;
                }

                if (j - c >= minRunCells)
                {
                    for (int k = c; k < j; k++)
                    {
                        s[(r * cols) + k] = (snap[((r - 1) * cols) + k] + snap[((r + 1) * cols) + k]) * 0.5f;
                    }
                }

                c = j;
            }
        }

        // COLUMN strips: same, perpendicular = LEFT/RIGHT, run vertical.
        Array.Copy(s, snap, s.Length);
        bool ColCandidate(int r, int c)
        {
            float v = snap[(r * cols) + c], le = snap[(r * cols) + c - 1], ri = snap[(r * cols) + c + 1];
            return IsValid(v) && IsValid(le) && IsValid(ri) && Math.Min(le, ri) - v > depthThresholdMeters;
        }

        for (int c = 1; c < cols - 1; c++)
        {
            int r = 0;
            while (r < rows)
            {
                if (!ColCandidate(r, c))
                {
                    r++;
                    continue;
                }

                int j = r;
                while (j < rows && ColCandidate(j, c))
                {
                    j++;
                }

                if (j - r >= minRunCells)
                {
                    for (int k = r; k < j; k++)
                    {
                        s[(k * cols) + c] = (snap[(k * cols) + c - 1] + snap[(k * cols) + c + 1]) * 0.5f;
                    }
                }

                r = j;
            }
        }

        return new DemRaster(cols, rows, raster.Bounds, s, noData);
    }

    // Walks `count` samples from `start` by `stride`, carrying the last valid value forward into holes.
    private static void FillRun(float[] s, int start, int count, int stride, Func<float, bool> isHole)
    {
        float last = 0f;
        bool haveLast = false;
        int i = start;
        for (int n = 0; n < count; n++, i += stride)
        {
            if (!isHole(s[i]))
            {
                last = s[i];
                haveLast = true;
            }
            else if (haveLast)
            {
                s[i] = last;
            }
        }
    }
}