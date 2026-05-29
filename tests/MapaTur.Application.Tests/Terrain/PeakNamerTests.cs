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

    private static DemRaster RampRaster()
    {
        // 20×20 over lon 19..21 / lat 49..50 (Gazetteer's Rysy @20.09 and Świnica @19.98 both inside),
        // elevation rising west→east just so SampleBilinear returns something finite and ordered.
        int cols = 20, rows = 20;
        var samples = new float[cols * rows];
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                samples[(r * cols) + c] = 1000f + (c * 50f);
            }
        }
        return new DemRaster(cols, rows, new MapBounds(new GeoPoint(49.0, 19.0), new GeoPoint(50.0, 21.0)), samples);
    }

    [Fact]
    public void MergeWithGazetteer_ShowsEveryInBoundsSummitNamed()
    {
        var merged = PeakNamer.MergeWithGazetteer(Array.Empty<TerrainPeak>(), Gazetteer, RampRaster());

        var names = merged.Select(p => p.Name).ToList();
        names.Should().Contain("Rysy");
        names.Should().Contain("Świnica");
        merged.Should().OnlyContain(p => !string.IsNullOrEmpty(p.Name));
    }

    [Fact]
    public void MergeWithGazetteer_ExcludesSummitOutsideRasterBounds()
    {
        // Far outside the 49..50 / 19..20 raster.
        var outside = new[] { new NamedSummit("Patagonia", new GeoPoint(-50.0, -73.0), 3000) };

        var merged = PeakNamer.MergeWithGazetteer(Array.Empty<TerrainPeak>(), outside, RampRaster());

        merged.Should().BeEmpty();
    }

    [Fact]
    public void MergeWithGazetteer_KeepsDetectedPeakFarFromAnySummit()
    {
        var detected = new[] { new TerrainPeak(new GeoPoint(49.5, 19.5), 1800) };

        var merged = PeakNamer.MergeWithGazetteer(detected, Gazetteer, RampRaster());

        merged.Should().Contain(p => p.Location.Latitude == 49.5 && string.IsNullOrEmpty(p.Name));
    }

    [Fact]
    public void MergeWithGazetteer_DropsDetectedPeakCoincidingWithNamedSummit()
    {
        // ~70 m from Rysy → deduped against the named summit so it isn't double-marked.
        var detected = new[] { new TerrainPeak(new GeoPoint(49.1800, 20.0885), 2495) };

        var merged = PeakNamer.MergeWithGazetteer(detected, Gazetteer, RampRaster());

        merged.Count(p => string.IsNullOrEmpty(p.Name)).Should().Be(0, "the only detected peak coincides with Rysy");
        merged.Count(p => p.Name == "Rysy").Should().Be(1);
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