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