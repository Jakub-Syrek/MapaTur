using FluentAssertions;

using MapaTur.Application.Routing;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Routing;

using NSubstitute;

namespace MapaTur.Application.Tests.Routing;

public sealed class PlanRouteUseCaseTests
{
    private static RouteRequest Request() =>
        new(new GeoPoint(49.0, 19.0), new GeoPoint(49.1, 19.1), RouteProfile.FastestTime);

    [Fact]
    public void Ctor_NullPlanner_Throws()
    {
        var act = () => new PlanRouteUseCase(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task HandleAsync_DelegatesToPlanner_AndReturnsItsResult()
    {
        var planner = Substitute.For<IRoutePlanner>();
        var request = Request();
        var route = new Route(new[]
        {
            new RouteSegment(request.Start, request.End, 100, 0, 0, 60),
        });
        planner.PlanRouteAsync(request, Arg.Any<CancellationToken>()).Returns(Task.FromResult<Route?>(route));
        var sut = new PlanRouteUseCase(planner);

        Route? result = await sut.HandleAsync(request);

        result.Should().BeSameAs(route);
        await planner.Received(1).PlanRouteAsync(request, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ReturnsNull_WhenPlannerFindsNoPath()
    {
        var planner = Substitute.For<IRoutePlanner>();
        planner.PlanRouteAsync(Arg.Any<RouteRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Route?>(null));
        var sut = new PlanRouteUseCase(planner);

        (await sut.HandleAsync(Request())).Should().BeNull();
    }
}
