using FluentAssertions;

using MapaTur.Domain.Geography;
using MapaTur.Domain.Tracks;

namespace MapaTur.Domain.Tests.Tracks;

public sealed class ElevationProfileTests
{
    [Fact]
    public void FromPoints_ReturnsEmptyWhenNoElevation()
    {
        var points = new[]
        {
            Point(49.0, 19.0, elevation: null),
            Point(49.1, 19.1, elevation: null),
        };

        var profile = ElevationProfile.FromPoints(points);

        profile.Should().Be(ElevationProfile.Empty);
    }

    [Fact]
    public void FromPoints_AggregatesAscentAndDescentCorrectly()
    {
        var points = new[]
        {
            Point(49.0, 19.0, elevation: 1000),
            Point(49.0, 19.1, elevation: 1200),
            Point(49.0, 19.2, elevation: 1100),
            Point(49.0, 19.3, elevation: 1500),
            Point(49.0, 19.4, elevation: 1400),
        };

        var profile = ElevationProfile.FromPoints(points);

        profile.MinElevationMeters.Should().Be(1000);
        profile.MaxElevationMeters.Should().Be(1500);
        profile.TotalAscentMeters.Should().Be(600);  // +200 + 400
        profile.TotalDescentMeters.Should().Be(200); // -100 - 100
    }

    [Fact]
    public void FromPoints_IgnoresPointsWithoutElevation()
    {
        var points = new[]
        {
            Point(49.0, 19.0, elevation: 1000),
            Point(49.0, 19.1, elevation: null),
            Point(49.0, 19.2, elevation: 1100),
        };

        var profile = ElevationProfile.FromPoints(points);

        profile.TotalAscentMeters.Should().Be(100);
    }

    private static TrackPoint Point(double latitude, double longitude, double? elevation)
    {
        return new TrackPoint(
            new GeoPoint(latitude, longitude, elevation),
            DateTimeOffset.UnixEpoch);
    }
}