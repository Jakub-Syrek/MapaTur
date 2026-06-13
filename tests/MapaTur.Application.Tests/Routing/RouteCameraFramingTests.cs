using FluentAssertions;

using MapaTur.Application.Routing;
using MapaTur.Domain.Geography;

namespace MapaTur.Application.Tests.Routing;

public sealed class RouteCameraFramingTests
{
    [Fact]
    public void Fit_CentresOnTheBoundingBoxMidpoint()
    {
        var polyline = new List<GeoPoint>
        {
            new(49.20, 19.90),
            new(49.30, 20.00),
            new(49.25, 20.10),
        };

        RouteFraming framing = RouteCameraFraming.Fit(polyline);

        framing.Center.Latitude.Should().BeApproximately((49.20 + 49.30) / 2, 1e-9);
        framing.Center.Longitude.Should().BeApproximately((19.90 + 20.10) / 2, 1e-9);
    }

    [Fact]
    public void Fit_DegenerateRoute_StillGivesAFiniteFramingDistance()
    {
        var single = new List<GeoPoint> { new(49.25, 20.00), new(49.25, 20.00) };

        RouteFraming framing = RouteCameraFraming.Fit(single);

        framing.DistanceMeters.Should().BeGreaterThan(0);
        double.IsFinite(framing.DistanceMeters).Should().BeTrue();
    }

    [Fact]
    public void Fit_WiderRoute_FramesFromFartherOut()
    {
        var narrow = new List<GeoPoint> { new(49.24, 19.99), new(49.26, 20.01) };
        var wide = new List<GeoPoint> { new(49.10, 19.80), new(49.40, 20.20) };

        double narrowDist = RouteCameraFraming.Fit(narrow).DistanceMeters;
        double wideDist = RouteCameraFraming.Fit(wide).DistanceMeters;

        wideDist.Should().BeGreaterThan(narrowDist);
    }

    [Fact]
    public void Fit_EmptyPolyline_Throws()
    {
        Action act = () => RouteCameraFraming.Fit(new List<GeoPoint>());

        act.Should().Throw<ArgumentException>();
    }
}