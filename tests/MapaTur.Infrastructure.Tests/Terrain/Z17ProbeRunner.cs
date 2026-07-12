using System.Globalization;
using System.Text;

using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;
using MapaTur.Infrastructure.Terrain;

namespace MapaTur.Infrastructure.Tests.Terrain;

/// <summary>
/// Dev/maintenance RUNNER (NOT a unit test) — Faza 0 of <c>docs/PLAN-sub-1m-geometry.md</c>: measures how
/// much REAL relief a z17 (≈0.78 m/px) GUGiK WCS fetch adds over the z16 (≈1.56 m/px) tiles we already have,
/// BEFORE committing to the whole-Tatra z17 download/bake. For each probe site (Orla Perć rock faces +
/// controls) it fetches the four z17 child tiles LIVE from the WCS (they land in the real
/// <c>dem-cache/gugik/17</c> cache, so nothing is wasted — Faza A reuses them) and prints, side by side:
///
/// <list type="bullet">
/// <item><b>self_dec / self_box (THE decision numbers)</b> — the SMOOTH-SURFACE-BUG.md §5 self-consistent
/// form computed on the z17 tile ALONE: decimate (every 2nd node) / 2×2-box-average it to z16 spacing,
/// Catmull-Rom it back up, RMS against the original. Measures exactly the relief living between 1.56 m and
/// 0.78 m sampling, with PERFECT self-registration — immune to WCS grid-registration conventions, temporal
/// dataset drift and fetch jitter, all of which contaminate any cross-fetch difference. The two variants
/// bracket the server's (unknown) 1 m→1.56 m decimation method (point-sample vs area-average). If the
/// server's z17 is secretly just an upsample of ≤1.56 m content, these correctly read ≈0.</item>
/// <item><b>rms_raw / rms_fit</b> — the cross-fetch residual z17 − CatmullRom(z16), raw and after a
/// per-child Nuth–Kääb co-registration fit (res ≈ bias + dx·slopeX + dy·slopeY). A pixel-vs-node grid
/// misinterpretation shifts each child by a constant ±half-z17-cell vector (per-quadrant sign), which the
/// fit cancels EXACTLY; the fitted <c>shift_m</c> also settles the registration question empirically
/// (≈0.55 m diagonal ⇒ the WCS grid is pixel-registered, our node reading is shifted).</item>
/// <item><b>drift_m</b> — the same z16 tile re-fetched LIVE into a throwaway cache and differenced against
/// the months-old cached copy on the shared grid: bounds temporal dataset drift + fetch nondeterminism
/// (must be ≈0 for the cross metric to mean anything).</item>
/// <item><b>gross</b> — cells rejected as garbage (|res| &gt; 50 m, or z17 exactly 0 where the parent reads
/// real terrain): a partially-zero WCS response passes the all-zero guard and would explode the RMS.
/// Nonzero gross ⇒ re-fetch that site before trusting its row.</item>
/// </list>
///
/// Gated behind <c>MAPATUR_PROBE_Z17=1</c> so a normal <c>dotnet test</c> never runs it (needs network + the
/// user's real cache). Run:
///   MAPATUR_PROBE_Z17=1 dotnet test tests/MapaTur.Infrastructure.Tests --filter FullyQualifiedName~Z17ProbeRunner
/// Optional env: MAPATUR_GUGIK_CACHE (cache root), MAPATUR_PROBE_OUT (report path).
/// </summary>
public sealed class Z17ProbeRunner
{
    private const string GateEnvVar = "MAPATUR_PROBE_Z17";

    // Same resolved desktop cache root as TatraBakeRunner (the comprehensive z16 set lives here).
    private const string DefaultGugikCacheRoot =
        @"C:\Users\jaqbs\AppData\Local\User Name\com.companyname.mapatur.app\Data\dem-cache\gugik";

    // No genuine 0.78-vs-1.56 m sampling difference approaches this — anything above is a corrupt cell
    // (partial-zero WCS response) and must not enter the statistics.
    private const double GrossResidualMeters = 50.0;

    // Probe sites — coordinates from the repo's CURATED catalogues (TatraSummits.cs / TatraPasses.cs), not
    // from memory: rugged Orla Perć rock + a big north wall (where sub-1.5 m edges live), a STEEP-BUT-SMOOTH
    // grass dome (isolates slope-proportional artefacts — registration/drift — from genuine roughness, which
    // the flat controls structurally cannot), and flat controls (meadow = binding null; lake = informational
    // only, LiDAR water fill is interpolated and may legitimately differ between request resolutions).
    private static readonly (string Name, double Lat, double Lon, string Kind)[] Sites =
    [
        ("Granaty (Orla Perc)", 49.226944, 20.033306, "rock"),
        ("Kozi Wierch", 49.218300, 20.028600, "rock"),
        ("Zamarla Turnia", 49.219417, 20.024472, "rock"),
        ("Krzyzne", 49.228652, 20.047278, "rock"),
        ("Mieguszowiecki (sciana N)", 49.187028, 20.059333, "rock"),
        ("Maloloczniak (trawa stromo)", 49.235806, 19.919306, "control-steep"),
        ("Hala Gasienicowa (laka)", 49.2430, 20.0070, "control-flat"),
        ("Morskie Oko (tafla)", 49.1980, 20.0710, "control-water"),
    ];

    [Fact]
    public async Task ProbeZ17InformationGain_OverCachedZ16()
    {
        if (Environment.GetEnvironmentVariable(GateEnvVar) != "1")
        {
            return; // intentionally inert unless explicitly enabled
        }

        string cacheRoot = Environment.GetEnvironmentVariable("MAPATUR_GUGIK_CACHE") ?? DefaultGugikCacheRoot;
        Directory.Exists(cacheRoot).Should().BeTrue(
            $"the real GUGiK cache must exist at '{cacheRoot}' (set MAPATUR_GUGIK_CACHE to override)");

        string reportPath = Environment.GetEnvironmentVariable("MAPATUR_PROBE_OUT")
            ?? Path.Combine(Path.GetDirectoryName(cacheRoot.TrimEnd(Path.DirectorySeparatorChar))!, "z17-probe-results.txt");

        // LIVE HttpClient — the whole point of the probe is fetching z17 from the WCS. Fetched TIFFs are
        // committed to the same cache the app and the future Faza A bake read, so probe traffic is reused.
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        var source = new GugikNmtDemTileSource(http, cacheRoot, tileSize: 256);

        // Second source over a THROWAWAY cache: re-fetches the already-cached z16 tiles live, so the probe
        // can measure temporal drift / fetch nondeterminism instead of asserting their absence.
        string driftCacheRoot = Path.Combine(Path.GetTempPath(), "mapatur-z17-probe-drift-cache");
        Directory.CreateDirectory(driftCacheRoot);
        var driftSource = new GugikNmtDemTileSource(http, driftCacheRoot, tileSize: 256);

        var inv = CultureInfo.InvariantCulture;
        var report = new StringBuilder();
        report.AppendLine("Z17 PROBE — zysk informacyjny z17 vs cache z16 (Faza 0, PLAN-sub-1m-geometry.md)");
        report.AppendLine(string.Create(inv, $"run: {DateTime.Now:yyyy-MM-dd HH:mm}, cache: {cacheRoot}"));
        report.AppendLine(new string('-', 132));
        report.AppendLine(
            "site                          kind           tiles  n_cells  rms_raw  rms_fit  p95fit  self_dec  self_box  shift_m  drift_m  gross  align_m");

        int sitesWithData = 0;
        foreach ((string name, double lat, double lon, string kind) in Sites)
        {
            (int x16, int y16) = SlippyTileMath.LonLatToTile(lon, lat, 16);
            var z16Key = new DemTileKey(16, x16, y16);
            DemRaster? z16 = await source.GetTileAsync(z16Key);
            if (z16 is null)
            {
                report.AppendLine(string.Create(inv, $"{name,-30}{kind,-15}NO z16 TILE — skipped"));
                continue;
            }

            var rawResiduals = new List<double>();
            var fitResiduals = new List<double>();
            var childShifts = new List<double>();
            var alignDiffs = new List<double>();
            double selfDecSumSq = 0;
            long selfDecN = 0;
            double selfBoxSumSq = 0;
            long selfBoxN = 0;
            int grossRejects = 0;
            int childTiles = 0;
            for (int dy = 0; dy < 2; dy++)
            {
                for (int dx = 0; dx < 2; dx++)
                {
                    var childKey = new DemTileKey(17, (2 * x16) + dx, (2 * y16) + dy);
                    DemRaster? z17 = await source.GetTileAsync(childKey);
                    if (z17 is null)
                    {
                        continue;
                    }

                    var samples = new List<ResidualSample>();
                    grossRejects += AccumulateResiduals(z16, z17, samples);
                    if (samples.Count == 0)
                    {
                        continue;
                    }

                    childTiles++;
                    // Co-registration fit PER CHILD — a pixel-vs-node misregistration shifts each child by a
                    // DIFFERENT (per-quadrant) vector, so a whole-site fit could not cancel it.
                    (List<double> deshifted, double fitDx, double fitDy, double _) = DeshiftResiduals(samples);
                    rawResiduals.AddRange(samples.Select(s => s.Residual));
                    fitResiduals.AddRange(deshifted);
                    childShifts.Add(Math.Sqrt((fitDx * fitDx) + (fitDy * fitDy)));
                    AccumulateAlignmentCheck(z16, z17, alignDiffs);

                    // Self-consistent metrics on the z17 tile alone (the decision numbers).
                    (double decSumSq, long decN) = SelfResidualSumSq(z17, Decimate2(z17), oddNodesOnly: true);
                    (double boxSumSq, long boxN) = SelfResidualSumSq(z17, BoxAverage2(z17), oddNodesOnly: false);
                    selfDecSumSq += decSumSq;
                    selfDecN += decN;
                    selfBoxSumSq += boxSumSq;
                    selfBoxN += boxN;
                }
            }

            if (childTiles == 0 || fitResiduals.Count == 0)
            {
                report.AppendLine(string.Create(inv, $"{name,-30}{kind,-15}NO z17 DATA — skipped"));
                continue;
            }

            sitesWithData++;
            // Percentiles over |residual| — the signed distribution is ~symmetric around 0, so a signed p95
            // would read only the positive tail and understate the two-sided structure.
            List<double> absFit = fitResiduals.Select(Math.Abs).OrderBy(d => d).ToList();
            double rmsRaw = Math.Sqrt(rawResiduals.Sum(d => d * d) / rawResiduals.Count);
            double rmsFit = Math.Sqrt(fitResiduals.Sum(d => d * d) / fitResiduals.Count);
            double p95Fit = absFit[(int)Math.Min(absFit.Count - 1L, (long)(0.95 * absFit.Count))];
            double selfDec = selfDecN > 0 ? Math.Sqrt(selfDecSumSq / selfDecN) : double.NaN;
            double selfBox = selfBoxN > 0 ? Math.Sqrt(selfBoxSumSq / selfBoxN) : double.NaN;
            double shift = childShifts.Average();
            double align = alignDiffs.Count > 0 ? Math.Sqrt(alignDiffs.Sum(d => d * d) / alignDiffs.Count) : double.NaN;
            double drift = await MeasureZ16DriftAsync(driftSource, z16Key, z16);

            report.AppendLine(string.Create(
                inv,
                $"{name,-30}{kind,-15}{childTiles,5}{fitResiduals.Count,9}{rmsRaw,9:F3}{rmsFit,9:F3}{p95Fit,8:F3}{selfDec,10:F3}{selfBox,10:F3}{shift,9:F3}{drift,9:F3}{grossRejects,7}{align,9:F3}"));
        }

        report.AppendLine(new string('-', 132));
        report.AppendLine("DECYZJA (PLAN-sub-1m-geometry.md Faza 0) — czytaj TAK:");
        report.AppendLine("  1. gross musi byc 0 (inaczej re-fetch siedliska); drift_m musi byc ~0 (inaczej cross-metryki niewazne).");
        report.AppendLine("  2. PIERWOTNE: MEDIANA self_dec po siedliskach rock. >= ~0.15 m => GO (z17 niesie realny relief);");
        report.AppendLine("     0.08-0.15 m => szara strefa (decyzja usera); < 0.08 m => NO-GO (WCS nie oddaje nic ponad z16).");
        report.AppendLine("     Kontrola: laka (control-flat) self_dec < 0.05 m, inaczej metryka/dane podejrzane. Jezioro = informacyjne.");
        report.AppendLine("  3. rms_fit powinien z grubsza zgadzac sie z self_dec (±30%); rms_raw >> rms_fit przy shift_m ~0.4-0.6 m");
        report.AppendLine("     = WCS zwraca siatke pixel-centre (nasza interpretacja node) — wazna wiedza dla Fazy A/B, nie blad danych.");
        report.AppendLine("  4. control-steep (trawa stromo) z self_dec ~0 a rms_fit duzym = artefakt proporcjonalny do nachylenia,");
        report.AppendLine("     ktorego fit nie zdjal — NIE ufac cross-metrykom, decydowac wylacznie po self_dec/self_box.");

        string text = report.ToString();
        Console.WriteLine(text);
        await File.WriteAllTextAsync(reportPath, text);
        Console.WriteLine($"[probe] report written: {reportPath}");

        sitesWithData.Should().BeGreaterThan(0, "the probe needs at least one site with z16 + z17 data");
    }

    // One probe sample: the raw residual plus the local slope of the UPSAMPLED parent surface — the slope is
    // the regressor of the Nuth–Kääb co-registration fit (see DeshiftResiduals).
    private readonly record struct ResidualSample(double Residual, double SlopeX, double SlopeY);

    // Residual z17 − CatmullRom(z16) over every z17 cell whose 4×4 parent footprint sits fully inside the
    // parent tile (interior margin — edge-clamped taps would read tile-border bias, not information).
    // Each sample also carries the upsampled surface's local gradient (central differences of the SAME
    // Catmull-Rom surface, one z17 cell apart) for the co-registration fit. Returns the number of GROSS
    // cells rejected as garbage (partial-zero / corrupt WCS content) — see GrossResidualMeters.
    private static int AccumulateResiduals(DemRaster z16, DemRaster z17, List<ResidualSample> samples)
    {
        int cols = z17.Columns;
        int rows = z17.Rows;
        double cellLon = (z17.East - z17.West) / (cols - 1);
        double cellLat = (z17.North - z17.South) / (rows - 1);
        double midLatRad = (z17.North + z17.South) * 0.5 * Math.PI / 180.0;
        double metersPerDegLon = 111_320.0 * Math.Cos(midLatRad);
        const double metersPerDegLat = 110_540.0;

        int gross = 0;
        for (int r = 0; r < rows; r++)
        {
            double latitude = z17.North - (r * cellLat);
            for (int c = 0; c < cols; c++)
            {
                float v17 = z17[c, r];
                if (v17 == z17.NoDataValue)
                {
                    continue;
                }

                double longitude = z17.West + (c * cellLon);
                // Margin 3: the gradient probes reach one z17 cell (= half a z16 cell) beyond the sample, so
                // keep the WHOLE probe cluster's 4×4 footprints interior.
                if (!ParentInteriorContains(z16, longitude, latitude, margin: 3.0))
                {
                    continue;
                }

                double up = DemRasterResampler.SampleCatmullRom(z16, longitude, latitude);
                double upE = DemRasterResampler.SampleCatmullRom(z16, longitude + cellLon, latitude);
                double upW = DemRasterResampler.SampleCatmullRom(z16, longitude - cellLon, latitude);
                double upN = DemRasterResampler.SampleCatmullRom(z16, longitude, latitude + cellLat);
                double upS = DemRasterResampler.SampleCatmullRom(z16, longitude, latitude - cellLat);
                if (up == z16.NoDataValue || upE == z16.NoDataValue || upW == z16.NoDataValue
                    || upN == z16.NoDataValue || upS == z16.NoDataValue)
                {
                    continue;
                }

                double residual = v17 - up;
                // Garbage gate: a partially-zero WCS response passes the all-zero guard, and each zero cell
                // would contribute ~ -2000 m of "residual". No real 0.78-vs-1.56 m difference gets near 50 m.
                if (Math.Abs(residual) > GrossResidualMeters || (v17 == 0f && up > 100.0))
                {
                    gross++;
                    continue;
                }

                double slopeX = (upE - upW) / (2.0 * cellLon * metersPerDegLon);
                double slopeY = (upN - upS) / (2.0 * cellLat * metersPerDegLat);
                samples.Add(new ResidualSample(residual, slopeX, slopeY));
            }
        }

        return gross;
    }

    /// <summary>
    /// Nuth–Kääb-style co-registration: least-squares fit <c>res ≈ bias + dx·slopeX + dy·slopeY</c> and
    /// return the de-shifted residuals plus the fitted (dx, dy, bias). A systematic horizontal shift between
    /// the two grids (whatever its cause — WCS pixel-vs-node registration, reprojection) shows up EXACTLY as
    /// slope-correlated residual, so removing the fit leaves only genuine sub-grid structure. The fitted dx/dy
    /// also settle the registration question empirically (≈±0.4 m with per-quadrant signs ⇒ pixel-registered).
    /// </summary>
    private static (List<double> Deshifted, double Dx, double Dy, double Bias) DeshiftResiduals(
        List<ResidualSample> samples)
    {
        // Normal equations for [bias, dx, dy] over rows [1, sx, sy].
        double n = samples.Count;
        double sSx = 0, sSy = 0, sSxSx = 0, sSySy = 0, sSxSy = 0, sR = 0, sRSx = 0, sRSy = 0;
        foreach (ResidualSample s in samples)
        {
            sSx += s.SlopeX;
            sSy += s.SlopeY;
            sSxSx += s.SlopeX * s.SlopeX;
            sSySy += s.SlopeY * s.SlopeY;
            sSxSy += s.SlopeX * s.SlopeY;
            sR += s.Residual;
            sRSx += s.Residual * s.SlopeX;
            sRSy += s.Residual * s.SlopeY;
        }

        // 3×3 symmetric solve (Cramer). Degenerate (flat tile: slopes ~0) → fall back to bias-only.
        double a11 = n, a12 = sSx, a13 = sSy;
        double a22 = sSxSx, a23 = sSxSy, a33 = sSySy;
        double det = (a11 * ((a22 * a33) - (a23 * a23)))
            - (a12 * ((a12 * a33) - (a23 * a13)))
            + (a13 * ((a12 * a23) - (a22 * a13)));
        double bias, dx, dy;
        if (Math.Abs(det) < 1e-9 * Math.Max(1.0, n))
        {
            bias = sR / n;
            dx = 0;
            dy = 0;
        }
        else
        {
            double detB = (sR * ((a22 * a33) - (a23 * a23)))
                - (a12 * ((sRSx * a33) - (a23 * sRSy)))
                + (a13 * ((sRSx * a23) - (a22 * sRSy)));
            double detX = (a11 * ((sRSx * a33) - (a23 * sRSy)))
                - (sR * ((a12 * a33) - (a23 * a13)))
                + (a13 * ((a12 * sRSy) - (sRSx * a13)));
            double detY = (a11 * ((a22 * sRSy) - (sRSx * a23)))
                - (a12 * ((a12 * sRSy) - (sRSx * a13)))
                + (sR * ((a12 * a23) - (a22 * a13)));
            bias = detB / det;
            dx = detX / det;
            dy = detY / det;
        }

        var deshifted = new List<double>(samples.Count);
        foreach (ResidualSample s in samples)
        {
            deshifted.Add(s.Residual - bias - (dx * s.SlopeX) - (dy * s.SlopeY));
        }

        return (deshifted, dx, dy, bias);
    }

    // ── Self-consistent metrics (SMOOTH-SURFACE-BUG.md §5) — computed on the z17 tile ALONE ────────────────

    /// <summary>
    /// Every 2nd node of <paramref name="fine"/> (even indices) as a coarse raster. The kept nodes sit at
    /// their EXACT original positions, so the coarse bounds end at the last even node — perfect
    /// self-registration by construction, the property the cross-fetch metric cannot have.
    /// </summary>
    private static DemRaster Decimate2(DemRaster fine)
    {
        int cols = ((fine.Columns - 1) / 2) + 1;
        int rows = ((fine.Rows - 1) / 2) + 1;
        var samples = new float[cols * rows];
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                samples[(r * cols) + c] = fine[2 * c, 2 * r];
            }
        }

        double east = fine.West + ((double)(2 * (cols - 1)) / (fine.Columns - 1) * (fine.East - fine.West));
        double south = fine.North - ((double)(2 * (rows - 1)) / (fine.Rows - 1) * (fine.North - fine.South));
        var bounds = new MapBounds(new GeoPoint(south, fine.West), new GeoPoint(fine.North, east));
        return new DemRaster(cols, rows, bounds, samples, fine.NoDataValue);
    }

    /// <summary>
    /// 2×2 box-average of <paramref name="fine"/> as a coarse raster whose nodes sit at the BLOCK CENTROIDS
    /// (half a fine cell in from the edges) — the bounds carry that half-cell offset so the coarse grid is
    /// sampled where its values actually live (a naive same-bounds declaration would inject the very
    /// half-cell shift artefact this probe exists to avoid). A block with any NoData cell becomes NoData.
    /// </summary>
    private static DemRaster BoxAverage2(DemRaster fine)
    {
        int cols = fine.Columns / 2;
        int rows = fine.Rows / 2;
        var samples = new float[cols * rows];
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                float v00 = fine[2 * c, 2 * r];
                float v01 = fine[(2 * c) + 1, 2 * r];
                float v10 = fine[2 * c, (2 * r) + 1];
                float v11 = fine[(2 * c) + 1, (2 * r) + 1];
                samples[(r * cols) + c] =
                    v00 == fine.NoDataValue || v01 == fine.NoDataValue
                    || v10 == fine.NoDataValue || v11 == fine.NoDataValue
                        ? fine.NoDataValue
                        : (v00 + v01 + v10 + v11) / 4f;
            }
        }

        double lonStep = (fine.East - fine.West) / (fine.Columns - 1);
        double latStep = (fine.North - fine.South) / (fine.Rows - 1);
        double west = fine.West + (0.5 * lonStep);
        double north = fine.North - (0.5 * latStep);
        double east = west + ((2 * (cols - 1)) * lonStep);
        double south = north - ((2 * (rows - 1)) * latStep);
        var bounds = new MapBounds(new GeoPoint(south, west), new GeoPoint(north, east));
        return new DemRaster(cols, rows, bounds, samples, fine.NoDataValue);
    }

    // Sum of squared reconstruction residuals fine − CatmullRom(coarse) over fine nodes whose coarse CR
    // footprint is fully interior. oddNodesOnly skips the even/even nodes the decimated grid kept verbatim
    // (CR reproduces them exactly — residual 0 there would only dilute the RMS).
    private static (double SumSq, long N) SelfResidualSumSq(DemRaster fine, DemRaster coarse, bool oddNodesOnly)
    {
        double sumSq = 0;
        long n = 0;
        int cols = fine.Columns;
        int rows = fine.Rows;
        for (int r = 0; r < rows; r++)
        {
            double latitude = fine.North - ((double)r / (rows - 1) * (fine.North - fine.South));
            for (int c = 0; c < cols; c++)
            {
                if (oddNodesOnly && c % 2 == 0 && r % 2 == 0)
                {
                    continue;
                }

                float v = fine[c, r];
                if (v == fine.NoDataValue)
                {
                    continue;
                }

                double longitude = fine.West + ((double)c / (cols - 1) * (fine.East - fine.West));
                if (!ParentInteriorContains(coarse, longitude, latitude, margin: 2.0))
                {
                    continue;
                }

                double up = DemRasterResampler.SampleCatmullRom(coarse, longitude, latitude);
                if (up == coarse.NoDataValue)
                {
                    continue;
                }

                double d = v - up;
                sumSq += d * d;
                n++;
            }
        }

        return (sumSq, n);
    }

    // Consistency check: 2×2-box-average the z17 child back to z16 resolution and difference it against the
    // parent's bilinear value at the block centre. Both grids are independent WCS resamples of the same 1 m
    // source, so this should be SMALL; align ≈ rms_raw on rock is the signature of a systematic grid shift
    // (see the report footer), align >> rms of a gross datum error.
    private static void AccumulateAlignmentCheck(DemRaster z16, DemRaster z17, List<double> diffs)
    {
        int cols = z17.Columns;
        int rows = z17.Rows;
        for (int r = 0; r + 1 < rows; r += 2)
        {
            for (int c = 0; c + 1 < cols; c += 2)
            {
                float v00 = z17[c, r];
                float v01 = z17[c + 1, r];
                float v10 = z17[c, r + 1];
                float v11 = z17[c + 1, r + 1];
                if (v00 == z17.NoDataValue || v01 == z17.NoDataValue
                    || v10 == z17.NoDataValue || v11 == z17.NoDataValue)
                {
                    continue;
                }

                double latitude = z17.North - ((r + 0.5) / (rows - 1) * (z17.North - z17.South));
                double longitude = z17.West + ((c + 0.5) / (cols - 1) * (z17.East - z17.West));
                if (!ParentInteriorContains(z16, longitude, latitude, margin: 2.0))
                {
                    continue;
                }

                double coarse = z16.SampleBilinear(longitude, latitude);
                if (coarse == z16.NoDataValue)
                {
                    continue;
                }

                diffs.Add(((v00 + v01 + v10 + v11) / 4.0) - coarse);
            }
        }
    }

    // Temporal-drift / fetch-nondeterminism bound: the SAME z16 tile re-fetched live, differenced against
    // the cached copy cell-by-cell on the shared grid (no resampling involved). NaN when the live fetch
    // failed or nothing overlapped.
    private static async Task<double> MeasureZ16DriftAsync(
        GugikNmtDemTileSource driftSource, DemTileKey key, DemRaster cached)
    {
        DemRaster? live = await driftSource.GetTileAsync(key);
        if (live is null || live.Columns != cached.Columns || live.Rows != cached.Rows)
        {
            return double.NaN;
        }

        double sumSq = 0;
        long n = 0;
        for (int r = 0; r < cached.Rows; r++)
        {
            for (int c = 0; c < cached.Columns; c++)
            {
                float a = cached[c, r];
                float b = live[c, r];
                if (a == cached.NoDataValue || b == live.NoDataValue)
                {
                    continue;
                }

                double d = b - a;
                sumSq += d * d;
                n++;
            }
        }

        return n > 0 ? Math.Sqrt(sumSq / n) : double.NaN;
    }

    private static bool ParentInteriorContains(DemRaster parent, double longitude, double latitude, double margin)
    {
        double rowFloat = (parent.North - latitude) / (parent.North - parent.South) * (parent.Rows - 1);
        double colFloat = (longitude - parent.West) / (parent.East - parent.West) * (parent.Columns - 1);
        return rowFloat >= margin && rowFloat <= parent.Rows - 1 - margin
            && colFloat >= margin && colFloat <= parent.Columns - 1 - margin;
    }
}