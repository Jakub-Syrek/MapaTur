using FluentAssertions;

using MapaTur.Application.Routing;
using MapaTur.Application.Terrain;
using MapaTur.Application.Trails;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Routing;
using MapaTur.Domain.Trails;

using NSubstitute;

namespace MapaTur.Application.Tests.Routing;

/// <summary>
/// Time-aware routing: in the mountains the shortest path is not the fastest. With elevation sampled onto the
/// graph, the "fastest time" profile (Tobler) must avoid a short-but-steep shortcut in favour of a longer-but-
/// flatter path; "shortest distance" still takes the direct steep line. Without an elevation source the graph is
/// flat and "fastest time" degenerates to shortest distance — which is exactly today's (broken) behaviour.
/// </summary>
public class TimeAwareRoutingTests
{
    private static readonly GeoPoint Start = new(49.2000, 20.0000);
    private static readonly GeoPoint SteepTop = new(49.2050, 20.0000); // +500 m hill on the direct line
    private static readonly GeoPoint Goal = new(49.2100, 20.0000);
    private static readonly GeoPoint West1 = new(49.2000, 19.9900);    // flat western detour
    private static readonly GeoPoint West2 = new(49.2100, 19.9900);

    private static Trail MakeTrail(long id, params GeoPoint[] points)
        => new(id, $"t{id}", new[] { new TrailMarking(PttkColor.None) }, points);

    private static ITrailRepository RepoWith(params Trail[] trails)
    {
        var repo = Substitute.For<ITrailRepository>();
        repo.FindIntersectingAsync(Arg.Any<MapBounds>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Trail>>(trails));
        return repo;
    }

    // 1500 m at the steep top, 1000 m everywhere else => the direct trail climbs 500 m and back.
    private sealed class HillElevation : IElevationSource
    {
        public double? ElevationAt(GeoPoint point)
            => Math.Abs(point.Latitude - 49.2050) < 1e-4 && Math.Abs(point.Longitude - 20.0000) < 1e-4
                ? 1500.0
                : 1000.0;
    }

    private static async Task<double?> PlannedDistanceAsync(ITrailRepository repo, IElevationSource? elevation, RouteProfile profile)
    {
        var planner = new TrailRoutePlanner(repo, elevation);
        Route? route = await planner.PlanRouteAsync(new RouteRequest(Start, Goal, profile));
        return route?.TotalDistanceMeters;
    }

    [Fact]
    public async Task FastestTime_WithElevation_TakesFlatLongPath_OverSteepShortcut()
    {
        ITrailRepository repo = RepoWith(
            MakeTrail(1, Start, SteepTop, Goal),     // ~1.1 km but climbs a 500 m hill
            MakeTrail(2, Start, West1, West2, Goal)); // ~2.6 km but flat

        double? fastest = await PlannedDistanceAsync(repo, new HillElevation(), RouteProfile.FastestTime);
        double? shortest = await PlannedDistanceAsync(repo, new HillElevation(), RouteProfile.ShortestDistance);

        fastest.Should().BeGreaterThan(2000.0, "the steep climb is slow, so the longer flat detour is faster");
        shortest.Should().BeLessThan(1500.0, "shortest distance ignores the climb and takes the direct line");
    }

    [Fact]
    public async Task FastestTime_WithoutElevation_DegeneratesToShortest()
    {
        ITrailRepository repo = RepoWith(
            MakeTrail(1, Start, SteepTop, Goal),
            MakeTrail(2, Start, West1, West2, Goal));

        double? fastest = await PlannedDistanceAsync(repo, elevation: null, RouteProfile.FastestTime);

        fastest.Should().BeLessThan(1500.0, "with no elevation Tobler is flat, so fastest == shortest");
    }
}