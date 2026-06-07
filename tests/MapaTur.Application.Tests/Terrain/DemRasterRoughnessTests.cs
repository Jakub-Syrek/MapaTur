using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Model 1 metric, tested in isolation (no LOD yet). <see cref="DemRasterRoughness.Roughness"/> measures
/// LOCAL curvature (jaggedness), so it separates SHARP terrain from merely DEEP terrain: a flat plane or a
/// planar slope reads ~0, a smooth (even deep) valley reads low, a ridge / wall reads high, a lone speckle
/// is dropped by the percentile, and no-data cells never invent roughness.
/// </summary>
public sealed class DemRasterRoughnessTests
{
    private static readonly MapBounds Bounds = new(new GeoPoint(49.0, 20.0), new GeoPoint(49.1, 20.1));

    private static DemRaster Make(int side, float[] samples, float noData = -9999f) =>
        new(side, side, Bounds, samples, noData);

    private static DemRaster Build(int side, Func<int, int, float> elevation, float noData = -9999f)
    {
        var samples = new float[side * side];
        for (int r = 0; r < side; r++)
        {
            for (int c = 0; c < side; c++)
            {
                samples[(r * side) + c] = elevation(c, r);
            }
        }

        return Make(side, samples, noData);
    }

    [Fact]
    public void Roughness_FlatPlane_IsZero()
    {
        DemRasterRoughness.Roughness(Build(7, (_, _) => 500f)).Should().BeApproximately(0.0, 1e-6);
    }

    [Fact]
    public void Roughness_PlanarSlope_IsZero()
    {
        // A linear ramp has zero curvature everywhere — roughness must not fire on a plain slope.
        DemRasterRoughness.Roughness(Build(7, (c, _) => 40f * c)).Should().BeApproximately(0.0, 1e-4);
    }

    [Fact]
    public void Roughness_SmoothButDeepValley_StaysLow_AndWellBelowARidge()
    {
        // Deep smooth bowl (up to ~90 m) — high RELIEF but low CURVATURE: must read low, not rough.
        DemRaster valley = Build(7, (c, r) => 5f * (((c - 3) * (c - 3)) + ((r - 3) * (r - 3))));
        // Sharp ridge: a single 100 m row — a real geometric edge.
        DemRaster ridge = Build(7, (_, r) => r == 3 ? 100f : 0f);

        double valleyRoughness = DemRasterRoughness.Roughness(valley);
        double ridgeRoughness = DemRasterRoughness.Roughness(ridge);

        valleyRoughness.Should().BeLessThan(15.0, "a smooth valley is gently curved everywhere");
        ridgeRoughness.Should().BeGreaterThan(30.0, "a sharp ridge is a real geometric edge");
        ridgeRoughness.Should().BeGreaterThan(valleyRoughness * 3.0, "depth ≠ roughness — the ridge dwarfs the valley");
    }

    [Fact]
    public void Roughness_LoneSpeckle_IsDroppedByThePercentile()
    {
        // Flat 50 m everywhere with one 200 m spike — a noisy pixel. The spike + its 4 neighbours are < 5%
        // of the cells, so the P95 aggregate drops them: roughness stays 0 instead of chasing the speckle.
        DemRaster speckled = Build(21, (c, r) => (c == 10 && r == 10) ? 200f : 50f);

        DemRasterRoughness.Roughness(speckled).Should().BeApproximately(0.0, 1e-4);
    }

    [Fact]
    public void Roughness_NoDataCells_AreIgnored_NotMeasuredAsRoughness()
    {
        // Flat terrain with a no-data hole: the sentinel must not become a giant curvature.
        DemRaster withHole = Build(5, (c, r) => (c == 2 && r == 2) ? -9999f : 100f);

        DemRasterRoughness.Roughness(withHole).Should().BeApproximately(0.0, 1e-4);
    }

    // --- stride: a cheaper scan (every N-th cell as a curvature centre, neighbours still ±1 native cell) ---
    // Stride preserves the METRIC SCALE — unlike subsampling the raster, which would space the neighbours N×
    // apart and inflate even a smooth valley into "roughness". These tests pin that distinction.

    [Fact]
    public void Roughness_Stride_KeepsTheMetricScale_SmoothValleyStaysLow()
    {
        // Same smooth bowl as the full-scan test: every interior cell has the SAME small curvature, so sampling
        // fewer of them changes nothing — the value stays low (a raster subsample would blow this up).
        DemRaster valley = Build(7, (c, r) => 5f * (((c - 3) * (c - 3)) + ((r - 3) * (r - 3))));

        DemRasterRoughness.Roughness(valley, stride: 2).Should().BeLessThan(15.0);
    }

    [Fact]
    public void Roughness_Stride_StillFiresOnARidge_FarAboveAValley()
    {
        DemRaster valley = Build(7, (c, r) => 5f * (((c - 3) * (c - 3)) + ((r - 3) * (r - 3))));
        DemRaster ridge = Build(7, (_, r) => r == 3 ? 100f : 0f);

        double valleyRoughness = DemRasterRoughness.Roughness(valley, stride: 2);
        double ridgeRoughness = DemRasterRoughness.Roughness(ridge, stride: 2);

        ridgeRoughness.Should().BeGreaterThan(15.0, "a ridge is still a sharp edge when sampled at a stride");
        ridgeRoughness.Should().BeGreaterThan(valleyRoughness * 3.0, "stride keeps rough ≫ smooth");
    }

    [Fact]
    public void Roughness_Stride_FlatStaysZero()
    {
        DemRasterRoughness.Roughness(Build(9, (_, _) => 500f), stride: 3).Should().BeApproximately(0.0, 1e-6);
    }

    [Fact]
    public void Roughness_StrideBelowOne_Throws()
    {
        Action act = () => DemRasterRoughness.Roughness(Build(5, (_, _) => 100f), stride: 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // --- neighbour distance: measure curvature over a WIDER baseline (±N cells), so ridge-scale jaggedness
    // registers. At 1-cell spacing real terrain reads ~0 (the boost stays inert); a coarser baseline lifts it.

    [Fact]
    public void Roughness_LargerNeighbourDistance_AmplifiesCurvature()
    {
        // A gently curved dome: curvature over a wider baseline grows (~distance²), so a coarser neighbour
        // distance lifts a feature that 1-cell sampling reads as nearly flat.
        DemRaster dome = Build(13, (c, r) => -0.5f * (((c - 6) * (c - 6)) + ((r - 6) * (r - 6))));

        double near = DemRasterRoughness.Roughness(dome, neighborDistance: 1);
        double wide = DemRasterRoughness.Roughness(dome, neighborDistance: 3);

        wide.Should().BeGreaterThan(near * 4.0, "a wider baseline measures curvature at a larger, ridge-relevant scale");
    }

    [Fact]
    public void Roughness_LargerNeighbourDistance_StillZeroOnAPlanarSlope()
    {
        // A wider baseline must still read a plain slope as flat — only curvature, never gradient, counts.
        DemRasterRoughness.Roughness(Build(13, (c, _) => 40f * c), neighborDistance: 3).Should().BeApproximately(0.0, 1e-3);
    }

    [Fact]
    public void Roughness_NeighbourDistanceBelowOne_Throws()
    {
        Action act = () => DemRasterRoughness.Roughness(Build(5, (_, _) => 100f), neighborDistance: 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}