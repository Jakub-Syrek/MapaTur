using FluentAssertions;

using MapaTur.Application.Routing;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Routing;

namespace MapaTur.Application.Tests.Routing;

/// <summary>
/// Multi-stop route planning: a tourist-map chain of waypoints (start → stop → … → end) is planned
/// leg by leg over the trail graph and concatenated into ONE route, with the per-leg distance/ascent/
/// duration aggregating up. A leg with no path fails the whole plan and reports which leg broke, so the
/// UI can say "no trail between stop 2 and stop 3" instead of a blank "no route".
/// </summary>
public sealed class MultiStopRoutePlannerTests
{
    private static GeoPoint P(double lat, double lon) => new(lat, lon);

    private static RouteSegment Seg(GeoPoint a, GeoPoint b, double dist = 100, double asc = 10, double desc = 5, double dur = 120)
        => new(a, b, dist, asc, desc, dur);

    // A stub planner that returns a canned one-segment route per (Start,End) pair, or null for pairs
    // listed as "no path".
    private sealed class StubPlanner : IRoutePlanner
    {
        private readonly HashSet<(double, double, double, double)> noPath;
        public List<RouteRequest> Requests { get; } = new();

        public StubPlanner(params (GeoPoint From, GeoPoint To)[] brokenLegs)
        {
            noPath = brokenLegs.Select(l => (l.From.Latitude, l.From.Longitude, l.To.Latitude, l.To.Longitude)).ToHashSet();
        }

        public Task<Route?> PlanRouteAsync(RouteRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var key = (request.Start.Latitude, request.Start.Longitude, request.End.Latitude, request.End.Longitude);
            if (noPath.Contains(key))
            {
                return Task.FromResult<Route?>(null);
            }

            return Task.FromResult<Route?>(new Route(new[] { Seg(request.Start, request.End) }));
        }
    }

    [Fact]
    public async Task PlanAsync_ThreeStops_ConcatenatesBothLegsIntoOneRoute()
    {
        var stub = new StubPlanner();
        var planner = new MultiStopRoutePlanner(stub);
        var stops = new[] { P(49.20, 20.00), P(49.21, 20.02), P(49.22, 20.04) };

        MultiStopRouteResult result = await planner.PlanAsync(stops, RouteProfile.FastestTime);

        result.FailedLegIndex.Should().BeNull();
        result.Route.Should().NotBeNull();
        result.Route!.Segments.Should().HaveCount(2, "one segment per leg, concatenated");
        result.Route.Start.Should().Be(stops[0]);
        result.Route.End.Should().Be(stops[2]);
        stub.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task PlanAsync_AggregatesDistanceAcrossLegs()
    {
        var planner = new MultiStopRoutePlanner(new StubPlanner());
        var stops = new[] { P(49.20, 20.00), P(49.21, 20.02), P(49.22, 20.04) };

        MultiStopRouteResult result = await planner.PlanAsync(stops, RouteProfile.FastestTime);

        result.Route!.TotalDistanceMeters.Should().Be(200, "two 100 m legs");
        result.Route.TotalAscentMeters.Should().Be(20);
    }

    [Fact]
    public async Task PlanAsync_BrokenMiddleLeg_FailsAndReportsItsIndex()
    {
        var stops = new[] { P(49.20, 20.00), P(49.21, 20.02), P(49.22, 20.04) };
        var planner = new MultiStopRoutePlanner(new StubPlanner((stops[1], stops[2])));

        MultiStopRouteResult result = await planner.PlanAsync(stops, RouteProfile.FastestTime);

        result.Route.Should().BeNull();
        result.FailedLegIndex.Should().Be(1, "the second leg (index 1) has no path");
    }

    [Fact]
    public async Task PlanAsync_TwoStops_IsASingleLeg()
    {
        var stub = new StubPlanner();
        var planner = new MultiStopRoutePlanner(stub);
        var stops = new[] { P(49.20, 20.00), P(49.21, 20.02) };

        MultiStopRouteResult result = await planner.PlanAsync(stops, RouteProfile.FastestTime);

        result.Route!.Segments.Should().HaveCount(1);
        stub.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task PlanAsync_FewerThanTwoStops_Throws()
    {
        var planner = new MultiStopRoutePlanner(new StubPlanner());

        Func<Task> act = () => planner.PlanAsync(new[] { P(49.20, 20.00) }, RouteProfile.FastestTime);

        await act.Should().ThrowAsync<ArgumentException>("a route needs at least a start and an end");
    }

    [Fact]
    public async Task PlanAsync_EachLegUsesTheRequestedProfile()
    {
        var stub = new StubPlanner();
        var planner = new MultiStopRoutePlanner(stub);
        var stops = new[] { P(49.20, 20.00), P(49.21, 20.02), P(49.22, 20.04) };

        await planner.PlanAsync(stops, RouteProfile.ShortestDistance);

        stub.Requests.Should().OnlyContain(r => r.Profile == RouteProfile.ShortestDistance);
    }
}