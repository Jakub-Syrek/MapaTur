namespace MapaTur.Application.Terrain;

/// <summary>
/// Post-bake gate for BORDER-CONCENTRATED height artefacts in the baked pyramid — the class the bit-identity
/// seam check is structurally blind to. Lesson (2026-07-15, the "tile grid / few-metre groove"): a per-tile
/// clamped downsample kernel biased the outer 1–2 rows of EVERY tile symmetrically toward its inside, so
/// adjacent tiles agreed bit-for-bit on their shared edge while a real kink (p95 |curvature residual| ≈ 1.0 m
/// at ±1 cell vs 0.44 m mid-tile background, Morskie Oko cirque) ran along every z17 border.
///
/// Method (checklist §A.2b's cross-section, applied at borders): for adjacent tile pairs, sample 17-point
/// height profiles ACROSS the shared border and compute the local curvature residual
/// <c>r_i = h_i − (h_{i−2} + h_{i+2})/2</c> at each offset from the border; aggregate p95 per offset over many
/// crossings and compare against the SAME statistic computed mid-tile (the terrain's own roughness floor).
/// Real terrain reads the same near a border as anywhere else — a border-concentrated excess is an artefact,
/// whatever stage introduced it. Pure math, no IO: the bake runner feeds tiles and asserts on the report.
/// </summary>
public sealed class TileBorderProfileAudit
{
    // Profile half-width in cells on each side of the border; residuals are computable for |offset| ≤ HALF−2.
    private const int Half = 8;

    // GUGiK flat-0 void marker (≤ 0.5 m — no real Tatra ground) — profiles touching it measure nothing.
    private const float FlatZeroFloor = 0.5f;

    private readonly List<double>[] borderResiduals = NewBuckets();
    private readonly List<double>[] controlResiduals = NewBuckets();
    private int borderProfiles;
    private int controlProfiles;

    private static List<double>[] NewBuckets()
    {
        var buckets = new List<double>[(2 * (Half - 2)) + 1];
        for (int i = 0; i < buckets.Length; i++)
        {
            buckets[i] = new List<double>();
        }

        return buckets;
    }

    /// <summary>Adds cross-border profiles for a west↔east adjacent pair (<paramref name="west"/> is the
    /// tile whose EAST edge is shared). Pixel-is-point: west's last column IS east's column 0.</summary>
    /// <param name="west">The western tile of the pair.</param>
    /// <param name="east">The eastern tile of the pair.</param>
    /// <param name="stride">Row step between sampled profiles (≥ 1).</param>
    public void AddEastPair(BakedDemTile west, BakedDemTile east, int stride = 4)
    {
        ArgumentNullException.ThrowIfNull(west);
        ArgumentNullException.ThrowIfNull(east);
        if (west.Columns != east.Columns || west.Rows != east.Rows)
        {
            return; // mixed-size pairs can't be aligned — skip rather than mis-measure
        }

        int cols = west.Columns;
        var profile = new float[2 * Half + 1];
        for (int r = 2; r < west.Rows - 2; r += Math.Max(1, stride))
        {
            for (int k = 0; k <= Half; k++)
            {
                profile[k] = west.Heights[(r * cols) + cols - 1 - Half + k]; // west: cols-1-Half .. cols-1
            }

            for (int k = 1; k <= Half; k++)
            {
                profile[Half + k] = east.Heights[(r * cols) + k];            // east: 1 .. Half (0 duplicates)
            }

            AccumulateProfile(profile, this.borderResiduals, (float)west.NoDataValue, ref this.borderProfiles);
        }
    }

    /// <summary>Adds cross-border profiles for a north↔south adjacent pair (<paramref name="north"/> is the
    /// tile whose SOUTH edge is shared). Pixel-is-point: north's last row IS south's row 0.</summary>
    /// <param name="north">The northern tile of the pair.</param>
    /// <param name="south">The southern tile of the pair.</param>
    /// <param name="stride">Column step between sampled profiles (≥ 1).</param>
    public void AddSouthPair(BakedDemTile north, BakedDemTile south, int stride = 4)
    {
        ArgumentNullException.ThrowIfNull(north);
        ArgumentNullException.ThrowIfNull(south);
        if (north.Columns != south.Columns || north.Rows != south.Rows)
        {
            return;
        }

        int cols = north.Columns;
        var profile = new float[2 * Half + 1];
        for (int c = 2; c < north.Columns - 2; c += Math.Max(1, stride))
        {
            for (int k = 0; k <= Half; k++)
            {
                profile[k] = north.Heights[((north.Rows - 1 - Half + k) * cols) + c];
            }

            for (int k = 1; k <= Half; k++)
            {
                profile[Half + k] = south.Heights[(k * cols) + c];
            }

            AccumulateProfile(profile, this.borderResiduals, (float)north.NoDataValue, ref this.borderProfiles);
        }
    }

    /// <summary>Adds mid-tile profiles from <paramref name="tile"/> — the terrain's own roughness floor the
    /// border statistic is compared against.</summary>
    /// <param name="tile">Any tile of the audited set.</param>
    /// <param name="stride">Row step between sampled profiles (≥ 1).</param>
    public void AddControl(BakedDemTile tile, int stride = 16)
    {
        ArgumentNullException.ThrowIfNull(tile);
        int cols = tile.Columns;
        int mid = cols / 2;
        if (mid - Half < 0 || mid + Half > cols - 1)
        {
            return; // tile too small to host a mid-tile window
        }

        var profile = new float[2 * Half + 1];
        for (int r = 2; r < tile.Rows - 2; r += Math.Max(1, stride))
        {
            for (int k = 0; k <= 2 * Half; k++)
            {
                profile[k] = tile.Heights[(r * cols) + mid - Half + k];
            }

            AccumulateProfile(profile, this.controlResiduals, (float)tile.NoDataValue, ref this.controlProfiles);
        }
    }

    /// <summary>Aggregates everything added so far into per-offset statistics.</summary>
    public TileBorderProfileReport Report()
    {
        int offsets = this.borderResiduals.Length;
        var border = new TileBorderProfileReport.OffsetStat[offsets];
        double controlP95 = 0;
        for (int i = 0; i < offsets; i++)
        {
            border[i] = new TileBorderProfileReport.OffsetStat(
                i - (Half - 2), Median(this.borderResiduals[i]), P95Abs(this.borderResiduals[i]));
            controlP95 = Math.Max(controlP95, P95Abs(this.controlResiduals[i]));
        }

        return new TileBorderProfileReport(border, controlP95, this.borderProfiles, this.controlProfiles);
    }

    private static void AccumulateProfile(float[] profile, List<double>[] buckets, float noData, ref int count)
    {
        foreach (float v in profile)
        {
            if (v == noData || v <= FlatZeroFloor)
            {
                return;
            }
        }

        count++;
        for (int i = 2; i < profile.Length - 2; i++)
        {
            buckets[i - 2].Add(profile[i] - ((profile[i - 2] + profile[i + 2]) / 2.0));
        }
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = new List<double>(values);
        sorted.Sort();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    private static double P95Abs(List<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var abs = values.ConvertAll(Math.Abs);
        abs.Sort();
        return abs[(int)(0.95 * (abs.Count - 1))];
    }
}

/// <summary>
/// The aggregated cross-border statistics of a <see cref="TileBorderProfileAudit"/> run: per-offset median and
/// p95 of the curvature residual around tile borders, against the mid-tile control p95.
/// </summary>
public sealed class TileBorderProfileReport
{
    /// <summary>Median and p95 |residual| of one profile offset.</summary>
    /// <param name="Offset">Cells from the border (0 = the shared line).</param>
    /// <param name="Median">Median residual in metres (signed; a systematic dip is negative).</param>
    /// <param name="P95Abs">95th percentile of |residual| in metres.</param>
    public readonly record struct OffsetStat(int Offset, double Median, double P95Abs);

    private readonly OffsetStat[] border;

    internal TileBorderProfileReport(
        OffsetStat[] border, double controlP95, int borderProfileCount, int controlProfileCount)
    {
        this.border = border;
        ControlP95 = controlP95;
        BorderProfileCount = borderProfileCount;
        ControlProfileCount = controlProfileCount;
    }

    /// <summary>Per-offset border statistics, ordered from the most negative offset to the most positive.</summary>
    public IReadOnlyList<OffsetStat> BorderByOffset => this.border;

    /// <summary>The terrain's own roughness floor: the worst per-offset p95 |residual| measured mid-tile.</summary>
    public double ControlP95 { get; }

    /// <summary>How many cross-border profiles produced samples (void-touching profiles are skipped).</summary>
    public int BorderProfileCount { get; }

    /// <summary>How many mid-tile control profiles produced samples.</summary>
    public int ControlProfileCount { get; }

    /// <summary>The worst border p95 |residual| within 2 cells of the border — where a kernel/repair artefact
    /// concentrates.</summary>
    public double WorstBorderP95
    {
        get
        {
            double worst = 0;
            foreach (OffsetStat s in this.border)
            {
                if (Math.Abs(s.Offset) <= 2)
                {
                    worst = Math.Max(worst, s.P95Abs);
                }
            }

            return worst;
        }
    }

    /// <summary>
    /// The gate: true when the border reads like the rest of the terrain — every offset within 2 cells of the
    /// border has p95 |residual| ≤ max(<paramref name="ratio"/> × <see cref="ControlP95"/>,
    /// <paramref name="floorMeters"/>). The absolute floor keeps a dead-flat region (control ≈ 0) from turning
    /// numeric noise into an "infinite ratio" flake.
    /// </summary>
    /// <param name="ratio">Allowed border-to-control p95 ratio (e.g. 1.3).</param>
    /// <param name="floorMeters">Absolute allowance in metres regardless of the control (e.g. 0.10).</param>
    public bool IsWithin(double ratio, double floorMeters)
        => WorstBorderP95 <= Math.Max(ratio * ControlP95, floorMeters);

    /// <summary>Console-friendly table of the per-offset statistics for the bake log.</summary>
    public string Format()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"border profiles: {BorderProfileCount}, control profiles: {ControlProfileCount}, control p95 |r|: {ControlP95:F4} m");
        sb.AppendLine("offset : median r [m]   p95 |r| [m]");
        foreach (OffsetStat s in this.border)
        {
            sb.AppendLine($"  {s.Offset,+3:+0;-0;+0} : {s.Median,+8:+0.0000;-0.0000}      {s.P95Abs:F4}{(s.Offset == 0 ? "  <-- border" : string.Empty)}");
        }

        return sb.ToString();
    }
}