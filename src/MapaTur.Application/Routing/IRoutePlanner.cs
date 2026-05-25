using MapaTur.Domain.Routing;

namespace MapaTur.Application.Routing;

/// <summary>
/// Application-level port for route planning. Implementations build (or reuse) a graph
/// from local trail data, then run a search and return the optimal route.
/// </summary>
public interface IRoutePlanner
{
    /// <summary>
    /// Plans a route between two geographic points.
    /// </summary>
    /// <param name="request">Route request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The planned route, or null if no path exists in the available trail data.</returns>
    Task<Route?> PlanRouteAsync(RouteRequest request, CancellationToken cancellationToken = default);
}
