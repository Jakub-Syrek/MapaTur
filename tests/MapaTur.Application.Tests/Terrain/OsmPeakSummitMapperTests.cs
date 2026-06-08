using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;

namespace MapaTur.Application.Tests.Terrain;

public sealed class OsmPeakSummitMapperTests
{
    private static OsmPeak Peak(long id, string name, double ele) =>
        new(id, name, new GeoPoint(49.2, 20.0), ele);

    [Fact]
    public void ToSummits_MapsNameLocationAndElevation()
    {
        var peaks = new[] { new OsmPeak(1, "Rysy", new GeoPoint(49.1795, 20.0882), 2501) };

        var summits = OsmPeakSummitMapper.ToSummits(peaks);

        summits.Should().HaveCount(1);
        summits[0].Name.Should().Be("Rysy");
        summits[0].Location.Latitude.Should().BeApproximately(49.1795, 1e-9);
        summits[0].Location.Longitude.Should().BeApproximately(20.0882, 1e-9);
        summits[0].ElevationMeters.Should().Be(2501);
    }

    [Fact]
    public void ToSummits_DropsUnnamedPeaks()
    {
        var peaks = new[] { Peak(1, string.Empty, 2000), Peak(2, "   ", 2000) };

        var summits = OsmPeakSummitMapper.ToSummits(peaks);

        summits.Should().BeEmpty();
    }

    [Fact]
    public void ToSummits_DropsPeaksWithoutElevation()
    {
        var peaks = new[] { new OsmPeak(1, "Bezimienna", new GeoPoint(49.2, 20.0), null) };

        var summits = OsmPeakSummitMapper.ToSummits(peaks);

        summits.Should().BeEmpty();
    }

    [Fact]
    public void ToSummits_DropsPeaksBelowDefaultThreshold()
    {
        var peaks = new[] { Peak(1, "Hrebienok", 1285), Peak(2, "Giewont", 1894) };

        var summits = OsmPeakSummitMapper.ToSummits(peaks);

        summits.Select(s => s.Name).Should().ContainSingle().Which.Should().Be("Giewont");
    }

    [Fact]
    public void ToSummits_HonoursCustomThreshold()
    {
        var peaks = new[] { Peak(1, "Hrebienok", 1285), Peak(2, "Giewont", 1894) };

        var summits = OsmPeakSummitMapper.ToSummits(peaks, minElevationMeters: 1200);

        summits.Should().HaveCount(2);
    }
}