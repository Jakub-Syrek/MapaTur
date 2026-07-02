using System.Linq;

using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

using Xunit;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// The ring-LOD base and live per-tile detail paths (<see cref="TerrainMesh3D.BuildAdaptiveTiles"/>) decimate the
/// raster by <see cref="PerTileLodDecision.SubsampleStep"/> — every vertex is a single POINT sample, not a box
/// average, so the cells between samples are simply dropped. This is the same class of loss the baked pipeline's
/// box-average downsampler has (fix B), so it must get the same recovery: since <c>BuildBlock</c> already holds
/// the FULL native raster (nothing pre-baked, nothing discarded ahead of time), it can compute the per-vertex
/// residual RMS ON THE FLY for a step-sampled tile — no detail grid, no re-bake, same "100% real amplitude" rule.
/// </summary>
public sealed class AdaptiveTileDetailTests
{
    private static readonly MapBounds Bounds = new(new GeoPoint(49.4, 19.4), new GeoPoint(49.6, 19.6));

    // Even columns = 1000 m, odd columns = 1100 m — a step-2 tile only ever SAMPLES even columns, so the
    // odd-column ridge is real relief hidden between samples.
    private static DemRaster RidgeEveryOtherColumn(int size = 8)
    {
        var samples = new float[size * size];
        for (int row = 0; row < size; row++)
        {
            for (int col = 0; col < size; col++)
            {
                samples[(row * size) + col] = (col % 2 == 0) ? 1000f : 1100f;
            }
        }

        return new DemRaster(size, size, Bounds, samples);
    }

    private static DemRaster Flat(float elevation = 1000f, int size = 8)
    {
        var samples = new float[size * size];
        Array.Fill(samples, elevation);
        return new DemRaster(size, size, Bounds, samples);
    }

    [Fact]
    public void BuildAdaptiveTiles_FlatRasterStep1_DetailIsZero()
    {
        // Native resolution samples EVERY cell — nothing is DISCARDED, so there is no residual to recover. On a
        // flat raster there is also no local variation to extrapolate a plausible sub-resolution micro-bump
        // from (NativeMicroDetail), so Detail stays exactly 0 — unlike a real, if flat-ish, native surface (see
        // BuildAdaptiveTiles_Step1WithLocalVariance_GetsModestNativeMicroDetail below).
        var plan = new[] { new PerTileLodDecision(ColStart: 0, RowStart: 0, Columns: 7, Rows: 7, SubsampleStep: 1) };

        var tiles = TerrainMesh3D.BuildAdaptiveTiles(Flat(), plan);

        tiles.Should().ContainSingle().Which.Detail.Should().OnlyContain(v => v == 0f);
    }

    [Fact]
    public void BuildAdaptiveTiles_Step1WithLocalVariance_GetsModestCappedNativeMicroDetail()
    {
        // 1 m LiDAR itself cannot capture sub-metre rock/scree microtexture — that is below the sensor's own
        // resolution, so no downsampling/re-bake fix could ever recover it from THIS data. A native (step=1)
        // vertex with real local variation instead gets a MODEST, capped synthetic micro-bump extrapolated from
        // its own small-scale roughness (NativeMicroDetail) — deliberately far below what the (large, 100 m)
        // variation here would read as if treated as measured relief, because it is extrapolation, not fact.
        var plan = new[] { new PerTileLodDecision(ColStart: 0, RowStart: 0, Columns: 7, Rows: 7, SubsampleStep: 1) };

        var tiles = TerrainMesh3D.BuildAdaptiveTiles(RidgeEveryOtherColumn(), plan);

        tiles.Should().ContainSingle().Which.Detail.Should().OnlyContain(
            v => v > 0f && v <= 0.9f,
            "native micro-detail must be non-zero where real local variation exists, but capped well below raw measured relief");
    }

    [Fact]
    public void BuildAdaptiveTiles_FlatRasterStep2_DetailIsZero()
    {
        // Step 2 discards half the columns, but the raster is flat — nothing real is being hidden.
        var plan = new[] { new PerTileLodDecision(ColStart: 0, RowStart: 0, Columns: 7, Rows: 7, SubsampleStep: 2) };

        var tiles = TerrainMesh3D.BuildAdaptiveTiles(Flat(), plan);

        tiles.Should().ContainSingle().Which.Detail.Should().OnlyContain(v => v == 0f);
    }

    [Fact]
    public void BuildAdaptiveTiles_Step2WithHiddenRidge_DetailRecoversTheRealResidual()
    {
        // Step 2 samples only even columns — the odd-column 1100 m ridge is real relief hidden between
        // samples. BuildBlock must recover it ON THE FLY from the raster it already holds (no detailGrid
        // supplied here), the same way the baked pipeline's downsampler would, so the shader can shade it.
        var plan = new[] { new PerTileLodDecision(ColStart: 0, RowStart: 0, Columns: 7, Rows: 7, SubsampleStep: 2) };

        var tiles = TerrainMesh3D.BuildAdaptiveTiles(RidgeEveryOtherColumn(), plan);

        tiles.Should().ContainSingle().Which.Detail.Should().OnlyContain(
            v => v > 40f && v < 55f,
            "the hidden 100 m column-to-column step should read back as ~47-50 m RMS in every sampled vertex's window");
    }

    [Fact]
    public void BuildAdaptiveTiles_LargerStep_DetailIsFadedRelativeToSmallerStep()
    {
        // Same alternating-ridge pattern (period 2, so the window's raw RMS is ~constant regardless of window
        // size) sampled at a near (step 4) and a far (step 32) step: the LOD selector only ever gives a large
        // step to ground far from the camera, so the far tile's detail must be faded down, not left equal or
        // amplified — isolating the fade from the underlying residual maths.
        DemRaster raster = RidgeEveryOtherColumn(size: 128);
        var nearPlan = new[] { new PerTileLodDecision(ColStart: 0, RowStart: 0, Columns: 127, Rows: 127, SubsampleStep: 4) };
        var farPlan = new[] { new PerTileLodDecision(ColStart: 0, RowStart: 0, Columns: 127, Rows: 127, SubsampleStep: 32) };

        float nearDetail = TerrainMesh3D.BuildAdaptiveTiles(raster, nearPlan).Single().Detail.Average();
        float farDetail = TerrainMesh3D.BuildAdaptiveTiles(raster, farPlan).Single().Detail.Average();

        farDetail.Should().BeLessThan(
            nearDetail * 0.75f,
            "step 32 (far) must fade the effect well below step 4 (near), not show equal or more detail");
    }

    [Fact]
    public void BuildAdaptiveTiles_NoDataInWindow_ExcludedFromResidual()
    {
        // One NoData cell sits inside a sample's window; the residual must be computed over the remaining
        // valid cells only, not corrupted by mixing the sentinel into the statistics, and must never throw
        // or produce NaN/negative values.
        var samples = new float[8 * 8];
        for (int row = 0; row < 8; row++)
        {
            for (int col = 0; col < 8; col++)
            {
                samples[(row * 8) + col] = (col % 2 == 0) ? 1000f : 1100f;
            }
        }

        samples[(0 * 8) + 1] = -9999f; // NoData inside column-0 vertex's window at row 0
        var raster = new DemRaster(8, 8, Bounds, samples, noDataValue: -9999f);
        var plan = new[] { new PerTileLodDecision(ColStart: 0, RowStart: 0, Columns: 7, Rows: 7, SubsampleStep: 2) };

        var tiles = TerrainMesh3D.BuildAdaptiveTiles(raster, plan);

        tiles.Should().ContainSingle().Which.Detail.Should().OnlyContain(v => !float.IsNaN(v) && v >= 0f);
    }
}