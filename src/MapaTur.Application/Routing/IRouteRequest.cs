using MapaTur.Domain.Geography;

namespace MapaTur.Application.Routing;

/// <summary>
/// Strategy for picking a cost function used to plan a route.
/// </summary>
public enum RouteProfile
{
    /// <summary>Optimise for shortest distance.</summary>
    ShortestDistance = 0,

    /// <summary>Optimise for fastest hiking time (Naismith/Tobler).</summary>
    FastestTime = 1,
}

/// <summary>
/// Input for the route planning use case.
/// </summary>
/// <param name="Start">Origin point in geographic coordinates.</param>
/// <param name="End">Destination point in geographic coordinates.</param>
/// <param name="Profile">Optimisation profile.</param>
/// <param name="IncludeOffTrailTracks">
/// When true, the planner augments the trail graph with the user's imported off-trail ("pozaszlaki") tracks
/// as penalised off-trail edges, so a route may use them. Off by default — the marked-trail graph is unchanged.
/// </param>
public sealed record RouteRequest(GeoPoint Start, GeoPoint End, RouteProfile Profile, bool IncludeOffTrailTracks = false);