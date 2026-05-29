using FluentAssertions;

using MapaTur.Domain.Geography;

namespace MapaTur.Domain.Tests.Geography;

public sealed class GeoPointTests
{
    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(49.2992, 19.9496)] // Kasprowy Wierch
    [InlineData(-90.0, 180.0)]
    [InlineData(90.0, -180.0)]
    public void Constructor_AcceptsValidCoordinates(double latitude, double longitude)
    {
        var point = new GeoPoint(latitude, longitude);

        point.Latitude.Should().Be(latitude);
        point.Longitude.Should().Be(longitude);
        point.ElevationMeters.Should().BeNull();
    }

    [Theory]
    [InlineData(-90.001, 0.0)]
    [InlineData(90.001, 0.0)]
    [InlineData(double.NaN, 0.0)]
    [InlineData(double.PositiveInfinity, 0.0)]
    public void Constructor_RejectsInvalidLatitude(double latitude, double longitude)
    {
        var act = () => new GeoPoint(latitude, longitude);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName(nameof(latitude));
    }

    [Theory]
    [InlineData(0.0, -180.001)]
    [InlineData(0.0, 180.001)]
    [InlineData(0.0, double.NaN)]
    public void Constructor_RejectsInvalidLongitude(double latitude, double longitude)
    {
        var act = () => new GeoPoint(latitude, longitude);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName(nameof(longitude));
    }

    [Fact]
    public void HaversineDistance_ToSelf_IsZero()
    {
        var point = new GeoPoint(49.2992, 19.9496);

        point.HaversineDistanceMetersTo(point).Should().BeApproximately(0.0, 1e-6);
    }

    [Fact]
    public void HaversineDistance_KasprowyToRysy_MatchesReference()
    {
        // Kasprowy Wierch -> Rysy: approx. 14.5 km great-circle distance.
        var kasprowy = new GeoPoint(49.2326, 19.9819);
        var rysy = new GeoPoint(49.1794, 20.0881);

        double distance = kasprowy.HaversineDistanceMetersTo(rysy);

        distance.Should().BeApproximately(9_660.0, 200.0);
    }

    [Fact]
    public void HaversineDistance_IsSymmetric()
    {
        var a = new GeoPoint(50.0, 19.0);
        var b = new GeoPoint(52.0, 21.0);

        double ab = a.HaversineDistanceMetersTo(b);
        double ba = b.HaversineDistanceMetersTo(a);

        ab.Should().BeApproximately(ba, 1e-6);
    }
}