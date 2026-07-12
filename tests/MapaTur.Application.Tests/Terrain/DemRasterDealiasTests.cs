using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Wariant 3 (user pick, 2026-07-10): <see cref="DemRasterDealias.Apply"/> — the z17 bake filter that kills
/// the WCS resample artefacts while keeping the real relief. Two stages: a mild global Gaussian (σ=0.5 cell)
/// that removes the sub-native grid "weave" (the ~0.1 m checkerboard visible as strukturka on flats and as
/// ±1–2 m organ-pipe flutes on walls), and a slope-GATED stronger smooth (σ=1.6 above ~55°, full by ~65°)
/// where a 2.5D DEM carries no reliable data anyway. Contracts: flat stays bit-flat, long-wavelength ledges
/// survive on walkable ground, grid-pitch noise is crushed, NoData and sub-coverage-floor cells neither move
/// nor poison their neighbours.
/// </summary>
public sealed class DemRasterDealiasTests
{
    private const float NoData = -9999f;

    // 64 nodes at ~0.78 m/cell (the z17 frame the filter is tuned for): 63 cells ≈ 49 m of ground.
    private static DemRaster Build(int side, Func<int, int, float> elevation)
    {
        var bounds = new MapBounds(new GeoPoint(49.2200, 20.0000), new GeoPoint(49.220444, 20.000676));
        var samples = new float[side * side];
        for (int r = 0; r < side; r++)
        {
            for (int c = 0; c < side; c++)
            {
                samples[(r * side) + c] = elevation(c, r);
            }
        }

        return new DemRaster(side, side, bounds, samples, NoData);
    }

    [Fact]
    public void Apply_FlatRaster_IsUnchanged()
    {
        DemRaster raster = Build(64, (_, _) => 1500f);

        DemRaster filtered = DemRasterDealias.Apply(raster);

        for (int i = 0; i < 64 * 64; i++)
        {
            filtered.Samples[i].Should().BeApproximately(1500f, 1e-3f);
        }
    }

    [Fact]
    public void Apply_GridWeaveOnGentleGround_IsCrushed()
    {
        // The strukturka: a ±0.12 m per-cell checkerboard riding a gentle 10° slope. After the filter its
        // amplitude must drop by well over half; the slope itself must survive untouched.
        DemRaster raster = Build(64, (c, r) => 1200f + (0.14f * c) + (((c + r) % 2 == 0) ? 0.12f : -0.12f));

        DemRaster filtered = DemRasterDealias.Apply(raster);

        double weave = 0;
        int n = 0;
        for (int r = 8; r < 56; r++)
        {
            for (int c = 8; c < 56; c++)
            {
                // The checkerboard reads as |z − mean(N4)|; the linear slope contributes 0 to it.
                float z = filtered.Samples[(r * 64) + c];
                float mean = (filtered.Samples[(r * 64) + c - 1] + filtered.Samples[(r * 64) + c + 1]
                    + filtered.Samples[((r - 1) * 64) + c] + filtered.Samples[((r + 1) * 64) + c]) / 4f;
                weave += Math.Abs(z - mean);
                n++;
            }
        }

        // σ=0.5 (the user-accepted sample's strength) attenuates the Nyquist checkerboard by ~65-70%.
        (weave / n).Should().BeLessThan(0.24 * 0.375, "the grid-pitch weave must lose at least ~65% of its amplitude");
        // The macro slope is untouched: a 40-cell horizontal run still climbs ~0.14 m/cell.
        (filtered.Samples[(32 * 64) + 52] - filtered.Samples[(32 * 64) + 12])
            .Should().BeApproximately(0.14f * 40f, 0.3f);
    }

    [Fact]
    public void Apply_RealLedgeOnWalkableGround_Survives()
    {
        // A 2 m terrace edge (long wavelength) on a moderate slope — the relief Faza A exists for. The mild
        // global sigma may round the lip slightly but the step height must survive.
        DemRaster raster = Build(64, (c, _) => 1200f + (0.3f * c) + (c >= 32 ? 2f : 0f));

        DemRaster filtered = DemRasterDealias.Apply(raster);

        float below = filtered.Samples[(32 * 64) + 24];
        float above = filtered.Samples[(32 * 64) + 40];
        (above - below - (0.3f * 16f)).Should().BeGreaterThan(1.8f, "the terrace must keep ≥90% of its height");
    }

    [Fact]
    public void Apply_ColumnFlutesOnAWall_AreMerged()
    {
        // The organ pipes: a ~67° slope (full gate, like the real Mylne Wrótka wall at mean ~68°) with
        // ±1.2 m alternating COLUMN offsets (the per-column gridding noise a 2.5D DEM carries on
        // near-vertical faces). The gate must crush the column-to-column jitter.
        DemRaster raster = Build(64, (c, r) => 1200f + (1.85f * r) + ((c % 2 == 0) ? 1.2f : -1.2f));

        DemRaster filtered = DemRasterDealias.Apply(raster);

        double jitter = 0;
        int n = 0;
        for (int r = 8; r < 56; r++)
        {
            for (int c = 8; c < 55; c++)
            {
                jitter += Math.Abs(filtered.Samples[(r * 64) + c + 1] - filtered.Samples[(r * 64) + c]);
                n++;
            }
        }

        (jitter / n).Should().BeLessThan(0.35, "±1.2 m column flutes must merge into a coherent face");
    }

    [Fact]
    public void Apply_NoDataCells_StayNoDataAndDoNotPoisonNeighbours()
    {
        DemRaster raster = Build(64, (c, r) => c >= 28 && c < 36 && r >= 28 && r < 36 ? NoData : 1500f);

        DemRaster filtered = DemRasterDealias.Apply(raster);

        filtered.Samples[(30 * 64) + 30].Should().Be(NoData, "voids are never filled by the filter");
        filtered.Samples[(32 * 64) + 26].Should().BeApproximately(
            1500f, 1e-3f, "a valid neighbour of a void averages only over VALID taps — no sentinel bleed");
    }

    [Fact]
    public void Apply_SubCoverageFloorZeros_AreExcludedFromTheBlur()
    {
        // GUGiK out-of-coverage halves are literal 0.0 — HoleBelow voids them AFTER this filter runs, so the
        // filter must already treat them as invalid or it would drag real terrain toward 0 at the border.
        DemRaster raster = Build(64, (c, _) => c < 20 ? 0f : 2000f);

        DemRaster filtered = DemRasterDealias.Apply(raster);

        filtered.Samples[(32 * 64) + 21].Should().BeApproximately(
            2000f, 1e-2f, "terrain beside a flat-0 coverage half must not be dragged toward 0");
        filtered.Samples[(32 * 64) + 10].Should().Be(0f, "sub-floor cells pass through untouched for HoleBelow");
    }
}