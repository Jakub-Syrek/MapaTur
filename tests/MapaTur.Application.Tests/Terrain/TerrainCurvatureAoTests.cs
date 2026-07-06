using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour of <see cref="TerrainCurvatureAo"/>: the per-vertex ambient-occlusion factor baked into the
/// mesh at build time (B of the accepted light&amp;shadow plan). The contract: flat and CONVEX ground is
/// fully open (1.0 — AO never brightens a ridge), CONCAVE ground (gully / bowl / couloir floor) darkens
/// with how steeply its surroundings rise, and the factor is floored so no vertex ever goes pitch black.
/// </summary>
public sealed class TerrainCurvatureAoTests
{
    private const float NoData = -9999f;

    // A small square raster with a given uniform cell size (metres) and a height function of (col, row).
    private static DemRaster Raster(int size, Func<int, int, float> height)
    {
        var samples = new float[size * size];
        for (int r = 0; r < size; r++)
        {
            for (int c = 0; c < size; c++)
            {
                samples[(r * size) + c] = height(c, r);
            }
        }

        // ~1 m cells at Tatra latitude: 1° lat ≈ 111 km, so size cells ≈ size/111000 degrees.
        double extentDeg = (double)size / 111_000.0;
        var bounds = new MapBounds(new GeoPoint(49.2, 20.0), new GeoPoint(49.2 + extentDeg, 20.0 + extentDeg));
        return new DemRaster(size, size, bounds, samples, NoData);
    }

    [Fact]
    public void should_return_fully_open_on_flat_ground()
    {
        DemRaster flat = Raster(128, (_, _) => 1500f);

        TerrainCurvatureAo.At(flat, 64, 64, cellSizeMeters: 1.0).Should().BeApproximately(1f, 0.001f);
    }

    [Fact]
    public void should_return_fully_open_on_a_ridge_top()
    {
        // A ridge: height falls away from the centre column — surroundings are LOWER. AO must not darken
        // (and must not "brighten" past 1 either).
        DemRaster ridge = Raster(128, (c, _) => 2000f - (Math.Abs(c - 64) * 2f));

        TerrainCurvatureAo.At(ridge, 64, 64, cellSizeMeters: 1.0).Should().BeApproximately(1f, 0.001f);
    }

    [Fact]
    public void should_darken_the_floor_of_a_bowl()
    {
        // A bowl: height rises away from the centre at ~45° — a strongly occluded point.
        DemRaster bowl = Raster(128, (c, r) =>
        {
            float d = MathF.Sqrt(((c - 64) * (c - 64)) + ((r - 64) * (r - 64)));
            return 1500f + d;
        });

        float ao = TerrainCurvatureAo.At(bowl, 64, 64, cellSizeMeters: 1.0);

        ao.Should().BeLessThan(0.8f, "a 45° bowl floor is strongly occluded");
        ao.Should().BeGreaterThanOrEqualTo(TerrainCurvatureAo.MinAo, "the floor keeps every vertex readable");
    }

    [Fact]
    public void should_darken_a_deep_bowl_more_than_a_shallow_one()
    {
        DemRaster shallow = Raster(128, (c, r) =>
        {
            float d = MathF.Sqrt(((c - 64) * (c - 64)) + ((r - 64) * (r - 64)));
            return 1500f + (d * 0.2f);
        });
        DemRaster deep = Raster(128, (c, r) =>
        {
            float d = MathF.Sqrt(((c - 64) * (c - 64)) + ((r - 64) * (r - 64)));
            return 1500f + (d * 1.5f);
        });

        float aoShallow = TerrainCurvatureAo.At(shallow, 64, 64, cellSizeMeters: 1.0);
        float aoDeep = TerrainCurvatureAo.At(deep, 64, 64, cellSizeMeters: 1.0);

        aoDeep.Should().BeLessThan(aoShallow);
    }

    [Fact]
    public void should_stay_open_next_to_nodata_and_at_the_raster_edge()
    {
        DemRaster holey = Raster(64, (c, r) => c < 8 ? NoData : 1500f);

        // Edge vertex + a vertex whose ring reaches into the NoData region: both fall back toward open
        // rather than inventing occlusion from invalid samples.
        TerrainCurvatureAo.At(holey, 0, 0, cellSizeMeters: 1.0).Should().BeApproximately(1f, 0.01f);
        TerrainCurvatureAo.At(holey, 9, 32, cellSizeMeters: 1.0).Should().BeGreaterThan(0.9f);
    }

    [Fact]
    public void should_treat_coarse_cells_with_the_same_metric_radii()
    {
        // The same 45° bowl sampled at 15 m cells (the coarse base) must still read as occluded — the
        // probe radii are METRIC, not cell counts, so the base and the 1 m tiles shade consistently.
        DemRaster bowl = Raster(64, (c, r) =>
        {
            float d = MathF.Sqrt(((c - 32) * (c - 32)) + ((r - 32) * (r - 32)));
            return 1500f + (d * 15f); // 45° in metres when cells are 15 m
        });

        float ao = TerrainCurvatureAo.At(bowl, 32, 32, cellSizeMeters: 15.0);

        ao.Should().BeLessThan(0.8f);
    }
}