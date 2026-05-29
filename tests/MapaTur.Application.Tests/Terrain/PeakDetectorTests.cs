using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class PeakDetectorTests
{
    // Bounds span lon 19..20 (W..E) and lat 49..50 (S..N). Row 0 = north edge.
    private static readonly MapBounds Bounds = new(
        new GeoPoint(49.0, 19.0),
        new GeoPoint(50.0, 20.0));

    private static DemRaster RasterWithBumps(int cols, int rows, float baseline, params (int Col, int Row, float Elevation)[] bumps)
    {
        var samples = new float[cols * rows];
        Array.Fill(samples, baseline);
        foreach (var (c, r, e) in bumps)
        {
            samples[(r * cols) + c] = e;
        }

        return new DemRaster(cols, rows, Bounds, samples);
    }

    [Fact]
    public void Detect_SingleBump_FindsExactlyOnePeak()
    {
        var raster = RasterWithBumps(5, 5, baseline: 1000f, (2, 2, 2000f));

        IReadOnlyList<TerrainPeak> peaks = PeakDetector.Detect(
            raster, new PeakDetectionOptions { NeighborhoodRadius = 1 });

        peaks.Should().HaveCount(1);
    }

    [Fact]
    public void Detect_SingleBump_ReportsBumpElevation()
    {
        var raster = RasterWithBumps(5, 5, baseline: 1000f, (2, 2, 2000f));

        IReadOnlyList<TerrainPeak> peaks = PeakDetector.Detect(
            raster, new PeakDetectionOptions { NeighborhoodRadius = 1 });

        peaks[0].ElevationMeters.Should().BeApproximately(2000.0, 1e-6);
    }

    [Fact]
    public void Detect_SingleBump_MapsCellToGeoCentre()
    {
        var raster = RasterWithBumps(5, 5, baseline: 1000f, (2, 2, 2000f));

        IReadOnlyList<TerrainPeak> peaks = PeakDetector.Detect(
            raster, new PeakDetectionOptions { NeighborhoodRadius = 1 });

        // Cell (2,2) of a 5×5 grid is the centre: lon 19.5, lat 49.5.
        peaks[0].Location.Longitude.Should().BeApproximately(19.5, 1e-9);
        peaks[0].Location.Latitude.Should().BeApproximately(49.5, 1e-9);
    }

    [Fact]
    public void Detect_FlatRaster_FindsNoPeaks()
    {
        var raster = RasterWithBumps(5, 5, baseline: 1000f);

        IReadOnlyList<TerrainPeak> peaks = PeakDetector.Detect(
            raster, new PeakDetectionOptions { NeighborhoodRadius = 1 });

        peaks.Should().BeEmpty("a flat surface has no strict local maximum");
    }

    [Fact]
    public void Detect_BumpBelowMinElevation_IsExcluded()
    {
        var raster = RasterWithBumps(5, 5, baseline: 1000f, (2, 2, 1200f));

        IReadOnlyList<TerrainPeak> peaks = PeakDetector.Detect(
            raster, new PeakDetectionOptions { NeighborhoodRadius = 1, MinElevationMeters = 1500.0 });

        peaks.Should().BeEmpty();
    }

    [Fact]
    public void Detect_MorePeaksThanCap_KeepsHighestOnly()
    {
        var raster = RasterWithBumps(
            7, 7, baseline: 1000f,
            (2, 2, 1500f), (4, 4, 1800f), (2, 4, 1700f));

        IReadOnlyList<TerrainPeak> peaks = PeakDetector.Detect(
            raster, new PeakDetectionOptions { NeighborhoodRadius = 1, MaxPeaks = 2 });

        peaks.Should().HaveCount(2);
    }

    [Fact]
    public void Detect_ReturnsPeaksHighestFirst()
    {
        var raster = RasterWithBumps(
            7, 7, baseline: 1000f,
            (2, 2, 1500f), (4, 4, 1800f), (2, 4, 1700f));

        IReadOnlyList<TerrainPeak> peaks = PeakDetector.Detect(
            raster, new PeakDetectionOptions { NeighborhoodRadius = 1 });

        peaks.Select(p => p.ElevationMeters).Should().BeInDescendingOrder();
    }

    [Fact]
    public void Detect_NoDataBump_IsIgnored()
    {
        // A no-data sentinel must never be reported as a summit even though it is
        // numerically larger than its neighbours would be if compared naively.
        var raster = RasterWithBumps(5, 5, baseline: 1000f, (2, 2, -9999f));

        IReadOnlyList<TerrainPeak> peaks = PeakDetector.Detect(
            raster, new PeakDetectionOptions { NeighborhoodRadius = 1 });

        peaks.Should().BeEmpty();
    }

    [Fact]
    public void Detect_DominanceRadiusMeters_SuppressesNearbyLowerPeak_IndependentOfResolution()
    {
        // Two bumps ~2 km apart in a 1°×1° (~111 km) box. With a 5 km dominance radius the lower
        // bump sits inside the higher one's window and must be suppressed — and this must hold at BOTH
        // grid resolutions, because the radius is expressed in metres, not cells. (A cell-based radius
        // would behave differently on the two grids — the bug behind the empty HD map.)
        var coarse = RasterWithBumps(20, 20, baseline: 1000f, (9, 9, 1800f), (10, 9, 1700f));
        var fine = RasterWithBumps(60, 60, baseline: 1000f, (29, 29, 1800f), (32, 29, 1700f));
        var opts = new PeakDetectionOptions { DominanceRadiusMeters = 5000.0 };

        PeakDetector.Detect(coarse, opts).Should().HaveCount(1, "5 km radius dominates the nearby lower bump");
        PeakDetector.Detect(fine, opts).Should().HaveCount(1, "same metres-based radius → same result at higher resolution");
    }

    [Fact]
    public void Detect_DominanceRadiusMeters_KeepsWellSeparatedPeaks()
    {
        // Two bumps far apart (~55 km, half the box) stay distinct under a 5 km radius.
        var raster = RasterWithBumps(60, 60, baseline: 1000f, (15, 15, 1800f), (45, 45, 1700f));

        PeakDetector.Detect(raster, new PeakDetectionOptions { DominanceRadiusMeters = 5000.0 })
            .Should().HaveCount(2);
    }

    [Fact]
    public void Detect_NullRaster_Throws()
    {
        Action act = () => PeakDetector.Detect(null!, new PeakDetectionOptions());

        act.Should().Throw<ArgumentNullException>();
    }
}