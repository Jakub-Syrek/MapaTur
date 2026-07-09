using FluentAssertions;

using MapaTur.Application.Routing;
using MapaTur.Application.Tracks;
using MapaTur.Application.Trails;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Routing;
using MapaTur.Domain.Tracks;
using MapaTur.Domain.Trails;

using NSubstitute;

namespace MapaTur.Application.Tests.Routing;

public sealed class TrailRoutePlannerTests
{
    private static readonly GeoPoint Start = new(49.00, 19.00);
    private static readonly GeoPoint Mid = new(49.05, 19.05);
    private static readonly GeoPoint End = new(49.10, 19.10);

    // Two short trails that do NOT touch each other; only an off-trail track bridges A1 → B0.
    private static readonly GeoPoint A0 = new(49.00, 19.00);
    private static readonly GeoPoint A1 = new(49.01, 19.01);
    private static readonly GeoPoint B0 = new(49.02, 19.02);
    private static readonly GeoPoint B1 = new(49.03, 19.03);

    private static ITrailRepository RepositoryReturning(params Trail[] trails)
    {
        var repo = Substitute.For<ITrailRepository>();
        repo.FindIntersectingAsync(Arg.Any<MapBounds>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Trail>>(trails));
        return repo;
    }

    private static ITrackRepository TrackRepositoryReturning(params Track[] tracks)
    {
        var repo = Substitute.For<ITrackRepository>();
        repo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Track>>(tracks));
        return repo;
    }

    private static Trail StraightTrail() =>
        new(1, "Ridge path", Array.Empty<TrailMarking>(), new[] { Start, Mid, End });

    private static Trail[] TwoDisconnectedTrails() =>
    [
        new(1, "A", Array.Empty<TrailMarking>(), new[] { A0, A1 }),
        new(2, "B", Array.Empty<TrailMarking>(), new[] { B0, B1 }),
    ];

    private static Track OffTrailTrack(params GeoPoint[] points) =>
        new(Guid.NewGuid(), "off", points.Select(p => new TrackPoint(p, DateTimeOffset.UnixEpoch)).ToList());

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

    [Fact]
    public async Task PlanRouteAsync_OffTrailEnabled_BridgesTwoDisconnectedTrails()
    {
        var trackRepo = TrackRepositoryReturning(OffTrailTrack(A1, B0));
        var sut = new TrailRoutePlanner(RepositoryReturning(TwoDisconnectedTrails()), elevation: null, offTrailTracks: trackRepo);

        var route = await sut.PlanRouteAsync(new RouteRequest(A0, B1, RouteProfile.ShortestDistance, IncludeOffTrailTracks: true));

        route.Should().NotBeNull();
        route!.Start.Latitude.Should().BeApproximately(A0.Latitude, 1e-6);
        route.End.Latitude.Should().BeApproximately(B1.Latitude, 1e-6);
    }

    [Fact]
    public async Task PlanRouteAsync_OffTrailDisabled_TrailsStayDisconnected_ReturnsNull()
    {
        var trackRepo = TrackRepositoryReturning(OffTrailTrack(A1, B0));
        var sut = new TrailRoutePlanner(RepositoryReturning(TwoDisconnectedTrails()), elevation: null, offTrailTracks: trackRepo);

        var route = await sut.PlanRouteAsync(new RouteRequest(A0, B1, RouteProfile.ShortestDistance, IncludeOffTrailTracks: false));

        route.Should().BeNull();
        // The off-trail store must not even be queried when the flag is off (keeps the on-trail path pristine).
        await trackRepo.DidNotReceive().GetAllAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PlanRouteAsync_OffTrailTrackOutsideSearchArea_IsIgnored()
    {
        // A bridging track, but far outside the Start/End search box → must not connect the two trails.
        var trackRepo = TrackRepositoryReturning(OffTrailTrack(new GeoPoint(52.00, 21.00), new GeoPoint(52.01, 21.01)));
        var sut = new TrailRoutePlanner(RepositoryReturning(TwoDisconnectedTrails()), elevation: null, offTrailTracks: trackRepo);

        var route = await sut.PlanRouteAsync(new RouteRequest(A0, B1, RouteProfile.ShortestDistance, IncludeOffTrailTracks: true));

        route.Should().BeNull();
    }
}