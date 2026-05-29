using FluentAssertions;

using MapaTur.Application.Routing;
using MapaTur.Application.Trails;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Routing;
using MapaTur.Domain.Trails;

using NSubstitute;

namespace MapaTur.Application.Tests.Routing;

public sealed class TrailRoutePlannerTests
{
    private static readonly GeoPoint Start = new(49.00, 19.00);
    private static readonly GeoPoint Mid = new(49.05, 19.05);
    private static readonly GeoPoint End = new(49.10, 19.10);

    private static ITrailRepository RepositoryReturning(params Trail[] trails)
    {
        var repo = Substitute.For<ITrailRepository>();
        repo.FindIntersectingAsync(Arg.Any<MapBounds>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Trail>>(trails));
        return repo;
    }

    private static Trail StraightTrail() =>
        new(1, "Ridge path", Array.Empty<TrailMarking>(), new[] { Start, Mid, End });

    [Fact]
    public void Ctor_NullRepository_Throws()
    {
        var act = () => new TrailRoutePlanner(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task PlanRouteAsync_NullRequest_Throws()
    {
        var sut = new TrailRoutePlanner(RepositoryReturning());

        await FluentActions.Awaiting(() => sut.PlanRouteAsync(null!)).Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task PlanRouteAsync_NoTrailsInArea_ReturnsNull()
    {
        var sut = new TrailRoutePlanner(RepositoryReturning());

        var result = await sut.PlanRouteAsync(new RouteRequest(Start, End, RouteProfile.ShortestDistance));

        result.Should().BeNull();
    }

    [Theory]
    [InlineData(RouteProfile.ShortestDistance)]
    [InlineData(RouteProfile.FastestTime)]
    public async Task PlanRouteAsync_WithConnectingTrail_ReturnsRouteBetweenEndpoints(RouteProfile profile)
    {
        var sut = new TrailRoutePlanner(RepositoryReturning(StraightTrail()));

        var route = await sut.PlanRouteAsync(new RouteRequest(Start, End, profile));

        route.Should().NotBeNull();
        route!.Segments.Should().NotBeEmpty();
        route.Start.Latitude.Should().BeApproximately(Start.Latitude, 1e-6);
        route.End.Latitude.Should().BeApproximately(End.Latitude, 1e-6);
    }
}
