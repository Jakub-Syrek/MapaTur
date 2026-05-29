using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class PeakNamerTests
{
    private static readonly NamedSummit Rysy = new("Rysy", new GeoPoint(49.1795, 20.0882), 2501);
    private static readonly NamedSummit Swinica = new("Świnica", new GeoPoint(49.2228, 19.9836), 2301);

    private static IReadOnlyList<NamedSummit> Gazetteer => new[] { Rysy, Swinica };

    // 60×60 grid over lon 19..21 / lat 49..50 (Gazetteer's Rysy @20.09 and Świnica @19.98 both inside).
    private const int GridSize = 60;
    private static readonly MapBounds WideBounds = new(new GeoPoint(49.0, 19.0), new GeoPoint(50.0, 21.0));

    private static GeoPoint CellGeo(int col, int row) => new(
        50.0 - ((double)row / (GridSize - 1) * 1.0),
        19.0 + ((double)col / (GridSize - 1) * 2.0));

    private static DemRaster FlatRaster(float baseline = 1000f, params (int Col, int Row, float Elevation)[] bumps)
    {
        var samples = new float[GridSize * GridSize];
        Array.Fill(samples, baseline);
        foreach (var (c, r, e) in bumps)
        {
            samples[(r * GridSize) + c] = e;
        }
        return new DemRaster(GridSize, GridSize, WideBounds, samples);
    }

    [Fact]
    public void MergeWithGazetteer_ShowsEveryInBoundsSummitNamed()
    {
        var merged = PeakNamer.MergeWithGazetteer(Array.Empty<TerrainPeak>(), Gazetteer, FlatRaster());

        var names = merged.Select(p => p.Name).ToList();
        names.Should().Contain("Rysy");
        names.Should().Contain("Świnica");
        merged.Should().OnlyContain(p => !string.IsNullOrEmpty(p.Name));
    }

    [Fact]
    public void MergeWithGazetteer_ExcludesSummitOutsideRasterBounds()
    {
        var outside = new[] { new NamedSummit("Patagonia", new GeoPoint(-50.0, -73.0), 3000) };

        var merged = PeakNamer.MergeWithGazetteer(Array.Empty<TerrainPeak>(), outside, FlatRaster());

        merged.Should().BeEmpty();
    }

    [Fact]
    public void MergeWithGazetteer_SnapsSummitToNearbyDemMaximum_NotThePointSample()
    {
        // The true summit (2300 m) sits one cell away from the gazetteer coordinate, which itself reads
        // only the 1000 m baseline. Snapping must report the summit's real height, not the point sample.
        var raster = FlatRaster(baseline: 1000f, (30, 30, 2300f));
        var summit = new[] { new NamedSummit("Test", CellGeo(31, 30), 9999) };

        var merged = PeakNamer.MergeWithGazetteer(Array.Empty<TerrainPeak>(), summit, raster);

        merged.Single(p => p.Name == "Test").ElevationMeters.Should().BeApproximately(2300.0, 1e-3);
    }

    [Fact]
    public void MergeWithGazetteer_SeatsOnDemButLabelsWithPublishedElevation()
    {
        // DEM max near the summit is 2300 m (smoothed), but the gazetteer publishes 2655 m. The marker
        // seats on 2300 (so it sits on the rendered terrain) yet labels with 2655 (the authoritative height).
        var raster = FlatRaster(baseline: 1000f, (30, 30, 2300f));
        var summit = new[] { new NamedSummit("Test", CellGeo(30, 30), 2655) };

        var peak = PeakNamer.MergeWithGazetteer(Array.Empty<TerrainPeak>(), summit, raster).Single(p => p.Name == "Test");

        peak.ElevationMeters.Should().BeApproximately(2300.0, 1e-3, "seated on the DEM maximum");
        peak.LabelElevationMeters.Should().Be(2655.0, "labelled with the published height");
    }

    [Fact]
    public void MergeWithGazetteer_KeepsDetectedPeakFarFromAnySummit()
    {
        var summit = new[] { new NamedSummit("Near", CellGeo(10, 10), 2000) };
        var detected = new[] { new TerrainPeak(CellGeo(50, 50), 1800) };

        var merged = PeakNamer.MergeWithGazetteer(detected, summit, FlatRaster());

        merged.Should().Contain(p => string.IsNullOrEmpty(p.Name));
    }

    [Fact]
    public void MergeWithGazetteer_DropsDetectedPeakCoincidingWithNamedSummit()
    {
        // Summit snaps to the 2300 m bump cell; a detected peak at that same cell is the same summit.
        var raster = FlatRaster(baseline: 1000f, (30, 30, 2300f));
        var summit = new[] { new NamedSummit("Test", CellGeo(30, 30), 2300) };
        var detected = new[] { new TerrainPeak(CellGeo(30, 30), 2300) };

        var merged = PeakNamer.MergeWithGazetteer(detected, summit, raster);

        merged.Count(p => p.Name == "Test").Should().Be(1);
        merged.Count(p => string.IsNullOrEmpty(p.Name)).Should().Be(0, "the detected peak coincides with the named summit");
    }

    [Fact]
    public void AssignNames_NullPeaks_Throws()
    {
        Action act = () => PeakNamer.AssignNames(null!, Gazetteer);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AssignNames_NullSummits_Throws()
    {
        var peaks = new[] { new TerrainPeak(new GeoPoint(49.18, 20.09), 2490) };

        Action act = () => PeakNamer.AssignNames(peaks, null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AssignNames_EmptyPeaks_ReturnsEmpty()
    {
        var result = PeakNamer.AssignNames(Array.Empty<TerrainPeak>(), Gazetteer);

        result.Should().BeEmpty();
    }

    [Fact]
    public void AssignNames_PeakWithinThreshold_GetsSummitName()
    {
        // ~70 m from Rysy — comfortably inside the default 800 m window.
        var peaks = new[] { new TerrainPeak(new GeoPoint(49.1800, 20.0885), 2495) };

        var result = PeakNamer.AssignNames(peaks, Gazetteer);

        result.Single().Name.Should().Be("Rysy");
    }

    [Fact]
    public void AssignNames_PeakBeyondThreshold_KeepsNullName()
    {
        // Mid-valley, kilometres from any gazetteer summit.
        var peaks = new[] { new TerrainPeak(new GeoPoint(49.30, 20.20), 1500) };

        var result = PeakNamer.AssignNames(peaks, Gazetteer);

        result.Single().Name.Should().BeNull();
    }

    [Fact]
    public void AssignNames_PicksNearestSummit_WhenMultipleInRange()
    {
        var dense = new[]
        {
            new NamedSummit("Far", new GeoPoint(49.1840, 20.0882), 2400),   // ~500 m north
            new NamedSummit("Near", new GeoPoint(49.1797, 20.0883), 2480),  // ~25 m
        };
        var peaks = new[] { new TerrainPeak(new GeoPoint(49.1795, 20.0882), 2495) };

        var result = PeakNamer.AssignNames(peaks, dense, maxMatchMeters: 1000.0);

        result.Single().Name.Should().Be("Near");
    }

    [Fact]
    public void AssignNames_PreservesLocationAndElevation()
    {
        var location = new GeoPoint(49.1800, 20.0885);
        var peaks = new[] { new TerrainPeak(location, 2495) };

        var result = PeakNamer.AssignNames(peaks, Gazetteer);

        result.Single().Location.Should().Be(location);
        result.Single().ElevationMeters.Should().Be(2495);
    }

    [Fact]
    public void AssignNames_RespectsCustomThreshold()
    {
        // ~330 m from Rysy: inside 800 m but outside a tightened 100 m window.
        var peaks = new[] { new TerrainPeak(new GeoPoint(49.1825, 20.0882), 2480) };

        var result = PeakNamer.AssignNames(peaks, Gazetteer, maxMatchMeters: 100.0);

        result.Single().Name.Should().BeNull();
    }
}