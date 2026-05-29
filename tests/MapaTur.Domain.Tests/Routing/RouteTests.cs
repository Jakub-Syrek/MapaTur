using FluentAssertions;

using MapaTur.Domain.Geography;
using MapaTur.Domain.Routing;

namespace MapaTur.Domain.Tests.Routing;

public sealed class RouteTests
{
    private static readonly GeoPoint A = new(49.0, 19.0);
    private static readonly GeoPoint B = new(49.1, 19.1);
    private static readonly GeoPoint C = new(49.2, 19.2);

    private static Route TwoSegmentRoute() => new(new[]
    {
        new RouteSegment(A, B, DistanceMeters: 100, AscentMeters: 50, DescentMeters: 0, DurationSeconds: 60),
        new RouteSegment(B, C, DistanceMeters: 200, AscentMeters: 0, DescentMeters: 30, DurationSeconds: 120),
    });

    [Fact]
    public void Ctor_AggregatesTotalsAcrossSegments()
    {
        Route route = TwoSegmentRoute();

        route.TotalDistanceMeters.Should().Be(300);
        route.TotalAscentMeters.Should().Be(50);
        route.TotalDescentMeters.Should().Be(30);
        route.TotalDurationSeconds.Should().Be(180);
    }

    [Fact]
    public void StartAndEnd_AreFirstFromAndLastTo()
    {
        Route route = TwoSegmentRoute();

        route.Start.Should().Be(A);
        route.End.Should().Be(C);
    }

    [Fact]
    public void ToPolyline_ReturnsFirstFromThenEverySegmentTo()
    {
        Route route = TwoSegmentRoute();

        route.ToPolyline().Should().Equal(A, B, C);
    }

    [Fact]
    public void Ctor_EmptySegments_Throws()
    {
        var act = () => new Route(Array.Empty<RouteSegment>());

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Ctor_NullSegments_Throws()
    {
        var act = () => new Route(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
