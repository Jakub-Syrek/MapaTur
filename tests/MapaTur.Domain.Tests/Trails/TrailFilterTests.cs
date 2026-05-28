using FluentAssertions;

using MapaTur.Domain.Geography;
using MapaTur.Domain.Trails;

namespace MapaTur.Domain.Tests.Trails;

public sealed class TrailFilterTests
{
    private static Trail BuildTrail(PttkColor colour, double lat, double lon)
        => new(
            id: 1,
            name: "T",
            markings: new[] { new TrailMarking(colour, "r:r:r") },
            geometry: new[]
            {
                new GeoPoint(lat, lon),
                new GeoPoint(lat + 0.001, lon + 0.001),
            });

    [Fact]
    public void IsColourEnabled_NotInEnabledSet_ReturnsFalse()
    {
        var filter = new TrailFilter();
        filter.EnabledColours.Add(PttkColor.Red);
        var blue = BuildTrail(PttkColor.Blue, 49.2, 19.9);

        filter.IsColourEnabled(blue).Should().BeFalse();
    }

    [Fact]
    public void IsColourEnabled_InEnabledSet_ReturnsTrue()
    {
        var filter = new TrailFilter();
        filter.EnabledColours.Add(PttkColor.Red);
        var red = BuildTrail(PttkColor.Red, 49.2, 19.9);

        filter.IsColourEnabled(red).Should().BeTrue();
    }

    [Fact]
    public void IsVisible_AllColoursDisabled_HidesEveryTrail()
    {
        var filter = new TrailFilter();
        var red = BuildTrail(PttkColor.Red, 49.2, 19.9);

        filter.IsVisible(red).Should().BeFalse();
    }

    [Fact]
    public void IsVisible_NoRegions_AllowsAnyLocation()
    {
        var filter = new TrailFilter();
        filter.EnabledColours.Add(PttkColor.Red);
        var farAway = BuildTrail(PttkColor.Red, -50.0, -75.0);

        filter.IsVisible(farAway).Should().BeTrue();
    }

    [Fact]
    public void IsVisible_RegionConfigured_TrailOutsideAllRegionsFails()
    {
        var filter = new TrailFilter();
        filter.EnabledColours.Add(PttkColor.Red);
        filter.EnabledRegions.Add(new MapBounds(new GeoPoint(49.1, 19.8), new GeoPoint(49.3, 20.2)));
        var bieszczady = BuildTrail(PttkColor.Red, 49.1, 22.6);

        filter.IsVisible(bieszczady).Should().BeFalse();
    }

    [Fact]
    public void IsVisible_RegionConfigured_TrailInsideRegionPasses()
    {
        var filter = new TrailFilter();
        filter.EnabledColours.Add(PttkColor.Red);
        filter.EnabledRegions.Add(new MapBounds(new GeoPoint(49.1, 19.8), new GeoPoint(49.3, 20.2)));
        var tatry = BuildTrail(PttkColor.Red, 49.2, 19.9);

        filter.IsVisible(tatry).Should().BeTrue();
    }

    [Fact]
    public void IsVisible_MultipleRegions_AnyMatchSuffices()
    {
        var filter = new TrailFilter();
        filter.EnabledColours.Add(PttkColor.Red);
        filter.EnabledRegions.Add(new MapBounds(new GeoPoint(49.1, 19.8), new GeoPoint(49.3, 20.2)));
        filter.EnabledRegions.Add(new MapBounds(new GeoPoint(49.0, 22.0), new GeoPoint(49.4, 22.8)));
        var bieszczady = BuildTrail(PttkColor.Red, 49.1, 22.6);

        filter.IsVisible(bieszczady).Should().BeTrue();
    }
}