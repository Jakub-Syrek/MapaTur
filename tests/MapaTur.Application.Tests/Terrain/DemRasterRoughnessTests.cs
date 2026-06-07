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
}