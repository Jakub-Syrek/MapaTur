using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Wariant 3 of the z17 artefact fix (user-picked from the sample sheet, 2026-07-10; TILE-PRODUCTION §2.5):
/// a bake-time filter for the finest fetched level that removes the two WCS resample artefacts the 0.78 m
/// grid exposed, while leaving the real relief the level exists for:
///
/// <list type="number">
/// <item><b>Global de-alias</b> — a mild NoData-aware Gaussian (σ = 0.5 cell) that kills the grid-locked
/// sub-native "weave" (per-cell checkerboard, ~0.1 m on gentle ground — the "strukturka" on scree flats),
/// wavelengths the 1.0 m source cannot genuinely carry at a 0.78 m grid. Measured cost on walkable ground:
/// ~0.09 m RMS ≈ exactly the probe controls' noise floor.</item>
/// <item><b>Slope-gated wall smooth</b> — above ~55° (full by ~65°) the surface blends into a stronger
/// Gaussian (σ = 1.6 cells): on near-vertical faces a 2.5D DEM carries quasi-random ±1–2 m per-column
/// gridding noise (LiDAR sees no ground on walls) that meshes into parallel "organ-pipe" flutes; merging
/// them costs nothing real because there is no reliable data there to lose.</item>
/// </list>
///
/// Runs INSIDE the margin-expanded bake window (before the seam weld and crop), so adjacent tiles filter
/// identically in their overlap — no tile-edge divergence (the kernel radius ≤ 5 cells is far inside the
/// one-tile margin). Validity: NoData cells and sub-coverage-floor cells (GUGiK flat-0 halves, later voided
/// by HoleBelow) are excluded from every kernel and pass through unchanged, so the blur can neither fill a
/// hole nor drag real terrain toward 0 at a coverage border.
/// </summary>
public static class DemRasterDealias
{
    /// <summary>Mild global Gaussian sigma (cells) — removes sub-native grid noise.</summary>
    public const double DealiasSigmaCells = 0.5;

    /// <summary>Wall-smoothing Gaussian sigma (cells), blended in by the slope gate.</summary>
    public const double WallSigmaCells = 1.6;

    /// <summary>Fall-line grade where the wall blend starts (tan; 1.4 ≈ 54°).</summary>
    public const double GateStartSlope = 1.4;

    /// <summary>Fall-line grade where the wall blend is fully engaged (tan; 2.2 ≈ 66°).</summary>
    public const double GateFullSlope = 2.2;

    /// <summary>
    /// Applies the two-stage filter and returns a new raster (input untouched). Cells that are NoData or
    /// below <paramref name="validFloorMeters"/> are excluded from all kernels and returned unchanged.
    /// </summary>
    /// <param name="raster">Source raster (typically the margin-expanded z17 bake window).</param>
    /// <param name="validFloorMeters">Coverage floor — cells at/below it are treated as invalid (the GUGiK
    /// out-of-coverage flat-0 class that HoleBelow voids later). Matches the bake's coverage floor.</param>
    /// <param name="cellSizeMeters">Ground cell pitch for the slope gate. Pass an EXPLICIT region-wide
    /// constant in the bake: adjacent tiles' margin windows carry their own approximate frames, and a
    /// bounds-derived scale differs between them by ulps — enough for the gate weight to break the
    /// bit-identical seam weld (the 2026-07-10 bake verify failure). ≤ 0 derives the scale from the
    /// raster's own bounds (fine for single-raster use and tests).</param>
    /// <exception cref="ArgumentNullException"><paramref name="raster"/> is null.</exception>
    public static DemRaster Apply(DemRaster raster, double validFloorMeters = 100.0, double cellSizeMeters = 0.0)
    {
        ArgumentNullException.ThrowIfNull(raster);

        int cols = raster.Columns;
        int rows = raster.Rows;
        float noData = raster.NoDataValue;
        var valid = new bool[cols * rows];
        var source = new float[cols * rows];
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                float v = raster[c, r];
                int i = (r * cols) + c;
                source[i] = v;
                valid[i] = v != noData && v > validFloorMeters;
            }
        }

        float[] dealiased = GaussianValid(source, valid, cols, rows, DealiasSigmaCells);
        float[] wall = GaussianValid(dealiased, valid, cols, rows, WallSigmaCells);

        // Cell size in metres for the slope gate. Explicit constant when provided (bake — see the param doc);
        // otherwise derived from this raster's bounds (mid-latitude scale, same convention as the mesh frame).
        double cellX, cellY;
        if (cellSizeMeters > 0.0)
        {
            cellX = cellSizeMeters;
            cellY = cellSizeMeters;
        }
        else
        {
            double midLatRad = (raster.North + raster.South) * 0.5 * Math.PI / 180.0;
            cellX = (raster.East - raster.West) / (cols - 1) * 111_320.0 * Math.Cos(midLatRad);
            cellY = (raster.North - raster.South) / (rows - 1) * 110_540.0;
        }

        var output = new float[cols * rows];
        for (int r = 0; r < rows; r++)
        {
            int rN = Math.Max(r - 1, 0);
            int rS = Math.Min(r + 1, rows - 1);
            for (int c = 0; c < cols; c++)
            {
                int i = (r * cols) + c;
                if (!valid[i])
                {
                    output[i] = source[i]; // voids and flat-0 pass through for HoleBelow
                    continue;
                }

                int cW = Math.Max(c - 1, 0);
                int cE = Math.Min(c + 1, cols - 1);
                // Gate on the DE-ALIASED surface's fall-line grade (the raw weave would inflate the slope).
                double dzdx = ValidDiff(dealiased, valid, (r * cols) + cE, (r * cols) + cW) / ((cE - cW) * cellX);
                double dzdy = ValidDiff(dealiased, valid, (rN * cols) + c, (rS * cols) + c) / ((rS - rN) * cellY);
                double grade = Math.Sqrt((dzdx * dzdx) + (dzdy * dzdy));
                double w = Math.Clamp((grade - GateStartSlope) / (GateFullSlope - GateStartSlope), 0.0, 1.0);
                output[i] = (float)((dealiased[i] * (1.0 - w)) + (wall[i] * w));
            }
        }

        return new DemRaster(cols, rows, raster.Bounds, output, noData);
    }

    private static double ValidDiff(float[] grid, bool[] valid, int a, int b)
        => valid[a] && valid[b] ? grid[a] - grid[b] : 0.0;

    // Separable NoData-aware Gaussian: weights renormalised over the VALID taps of each window, invalid
    // centre cells returned untouched. Radius 3σ keeps >99.7% of the kernel mass.
    private static float[] GaussianValid(float[] source, bool[] valid, int cols, int rows, double sigmaCells)
    {
        int radius = Math.Max(1, (int)Math.Ceiling(3.0 * sigmaCells));
        var kernel = new double[(2 * radius) + 1];
        for (int k = -radius; k <= radius; k++)
        {
            kernel[k + radius] = Math.Exp(-(k * k) / (2.0 * sigmaCells * sigmaCells));
        }

        var horizontal = new float[cols * rows];
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                int i = (r * cols) + c;
                if (!valid[i])
                {
                    horizontal[i] = source[i];
                    continue;
                }

                double sum = 0, weight = 0;
                for (int k = -radius; k <= radius; k++)
                {
                    int cc = c + k;
                    if (cc < 0 || cc >= cols || !valid[(r * cols) + cc])
                    {
                        continue;
                    }

                    sum += source[(r * cols) + cc] * kernel[k + radius];
                    weight += kernel[k + radius];
                }

                horizontal[i] = weight > 0 ? (float)(sum / weight) : source[i];
            }
        }

        var output = new float[cols * rows];
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                int i = (r * cols) + c;
                if (!valid[i])
                {
                    output[i] = source[i];
                    continue;
                }

                double sum = 0, weight = 0;
                for (int k = -radius; k <= radius; k++)
                {
                    int rr = r + k;
                    if (rr < 0 || rr >= rows || !valid[(rr * cols) + c])
                    {
                        continue;
                    }

                    sum += horizontal[(rr * cols) + c] * kernel[k + radius];
                    weight += kernel[k + radius];
                }

                output[i] = weight > 0 ? (float)(sum / weight) : source[i];
            }
        }

        return output;
    }
}