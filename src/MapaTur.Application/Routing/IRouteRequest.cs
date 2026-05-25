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
public sealed record RouteRequest(GeoPoint Start, GeoPoint End, RouteProfile Profile);
