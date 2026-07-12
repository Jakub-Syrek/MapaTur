using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// <see cref="DemRasterResampler.SampleCatmullRom"/> — the smooth (bicubic) point sampler used by the z17
/// probe (Faza 0 of the sub-1 m plan) and later by the synthetic-tile upsampler (Faza B). Contract: exact at
/// grid nodes, exact on planes (so a linear slope gains NO fake relief), smoother than bilinear inside a
/// cell, and NoData-safe (any sentinel in the 4×4 footprint falls back to the NoData-aware bilinear blend —
/// never lets the sentinel poison the interpolation).
/// </summary>
public sealed class DemRasterResamplerTests
{
    private const float NoData = -9999f;
    private static readonly MapBounds Bounds = new(new GeoPoint(49.0, 20.0), new GeoPoint(49.1, 20.1));

    private static DemRaster Build(int side, Func<int, int, float> elevation)
    {
        var samples = new float[side * side];
        for (int r = 0; r < side; r++)
        {
            for (int c = 0; c < side; c++)
            {
                samples[(r * side) + c] = elevation(c, r);
            }
        }

        return new DemRaster(side, side, Bounds, samples, NoData);
    }

    // Node (c, r) → the WGS-84 point it is registered on (same node registration as DemRaster.SampleBilinear:
    // node 0 on the west/north edge, node side-1 on the east/south edge).
    private static (double Lon, double Lat) NodeGeo(DemRaster raster, double c, double r)
    {
        double lon = raster.West + (c / (raster.Columns - 1) * (raster.East - raster.West));
        double lat = raster.North - (r / (raster.Rows - 1) * (raster.North - raster.South));
        return (lon, lat);
    }

    [Fact]
    public void SampleCatmullRom_AtGridNodes_ReturnsTheNodeValueExactly()
    {
        // Catmull-Rom interpolates THROUGH its control points — at a node the sampler must return the node.
        DemRaster raster = Build(6, (c, r) => 900f + (13f * c) + (7f * r) + (c * r * 0.5f));
        for (int r = 0; r < 6; r++)
        {
            for (int c = 0; c < 6; c++)
            {
                (double lon, double lat) = NodeGeo(raster, c, r);
                DemRasterResampler.SampleCatmullRom(raster, lon, lat)
                    .Should().BeApproximately(raster[c, r], 1e-3, $"node ({c},{r}) must reproduce exactly");
            }
        }
    }

    [Fact]
    public void SampleCatmullRom_OnAPlane_ReproducesThePlaneEverywhere()
    {
        // Linear precision: a planar slope must gain NO synthetic curvature from the resampler (otherwise the
        // z17-vs-upsampled-z16 residual metric would read fake "information" on every smooth hillside).
        DemRaster raster = Build(7, (c, r) => 1200f + (10f * c) - (4f * r));
        foreach ((double c, double r) in new[] { (1.5, 1.5), (2.25, 4.75), (3.5, 2.0), (4.9, 4.1) })
        {
            (double lon, double lat) = NodeGeo(raster, c, r);
            double expected = 1200.0 + (10.0 * c) - (4.0 * r);
            DemRasterResampler.SampleCatmullRom(raster, lon, lat).Should().BeApproximately(expected, 1e-3);
        }
    }

    [Fact]
    public void SampleCatmullRom_MidCellBesideALonePeak_MatchesTheCatmullRomKernel()
    {
        // 1D Catmull-Rom through p = [0, 1, 0, 0] at t = 0.5 is 0.5625; the 2D sampler is separable and the
        // sample sits ON the peak's row (t_row = 0), so the value must equal the 1D kernel exactly.
        DemRaster raster = Build(7, (c, r) => c == 3 && r == 3 ? 1f : 0f);
        (double lon, double lat) = NodeGeo(raster, 3.5, 3.0);
        DemRasterResampler.SampleCatmullRom(raster, lon, lat).Should().BeApproximately(0.5625, 1e-6);
    }

    [Fact]
    public void SampleCatmullRom_OnASmoothBowl_IsCloserToTheTruthThanBilinear()
    {
        // Quadratic bowl: bilinear chords the parabola (error 0.25 at mid-cell); Catmull-Rom must do better —
        // that superiority is the whole reason the probe upsamples with it instead of bilinear.
        DemRaster raster = Build(7, (c, r) => ((c - 3f) * (c - 3f)) + ((r - 3f) * (r - 3f)));
        (double lon, double lat) = NodeGeo(raster, 3.5, 3.0);
        const double truth = 0.25; // (3.5−3)² + 0
        double catmull = DemRasterResampler.SampleCatmullRom(raster, lon, lat);
        double bilinear = raster.SampleBilinear(lon, lat);
        Math.Abs(catmull - truth).Should().BeLessThan(Math.Abs(bilinear - truth));
    }

    [Fact]
    public void SampleCatmullRom_WithNoDataInTheFootprint_FallsBackToBilinear()
    {
        // A sentinel tap must never enter the cubic blend (it would swing the result by thousands of metres);
        // the sampler degrades to the NoData-aware bilinear instead.
        DemRaster raster = Build(7, (c, r) => c == 2 && r == 2 ? NoData : 500f + (2f * c));
        (double lon, double lat) = NodeGeo(raster, 3.5, 3.5); // 4×4 footprint spans cols/rows 2..5 → hits the hole
        double expected = raster.SampleBilinear(lon, lat);
        DemRasterResampler.SampleCatmullRom(raster, lon, lat).Should().BeApproximately(expected, 1e-6);
    }

    [Fact]
    public void SampleCatmullRom_AllNoDataFootprint_SurfacesNoData()
    {
        DemRaster raster = Build(6, (_, _) => NoData);
        (double lon, double lat) = NodeGeo(raster, 2.5, 2.5);
        DemRasterResampler.SampleCatmullRom(raster, lon, lat).Should().Be(NoData);
    }

    [Fact]
    public void SampleCatmullRom_OutsideTheBounds_ClampsToTheEdgeLikeBilinear()
    {
        DemRaster raster = Build(6, (c, r) => 100f + c + r);
        // Beyond the east edge, on row 2's latitude → must clamp to the (5, 2) edge node.
        (double lonEast, double lat2) = NodeGeo(raster, 5.0, 2.0);
        DemRasterResampler.SampleCatmullRom(raster, lonEast + 0.05, lat2)
            .Should().BeApproximately(raster[5, 2], 1e-3);
    }
}