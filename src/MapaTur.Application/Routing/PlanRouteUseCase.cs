using MapaTur.Domain.Routing;

namespace MapaTur.Application.Routing;

/// <summary>
/// Use case: plans an optimal route between two geographic points.
/// </summary>
public sealed class PlanRouteUseCase
{
    private readonly IRoutePlanner planner;

    /// <summary>
    /// Initializes a new use case.
    /// </summary>
    /// <param name="planner">Route planner implementation.</param>
    public PlanRouteUseCase(IRoutePlanner planner)
    {
        ArgumentNullException.ThrowIfNull(planner);
        this.planner = planner;
    }

    /// <summary>
    /// Plans a route.
    /// </summary>
    /// <param name="request">Route request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The planned route, or null if no path exists.</returns>
    public Task<Route?> HandleAsync(RouteRequest request, CancellationToken cancellationToken = default)
    {
        return planner.PlanRouteAsync(request, cancellationToken);
    }
}