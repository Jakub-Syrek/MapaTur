using MapaTur.Application.Terrain;
using MapaTur.Application.Trails;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Routing;
using MapaTur.Domain.Trails;
using MapaTur.Routing;
using MapaTur.Routing.Costs;
using MapaTur.Routing.Graph;

namespace MapaTur.Application.Routing;

/// <summary>
/// Default route planner: queries the trail repository for trails in the area around
/// start and end, builds an in-memory graph, snaps both endpoints to the nearest graph
/// node, and runs A* with the cost function chosen by the request profile.
/// </summary>
public sealed class TrailRoutePlanner : IRoutePlanner
{
    private const double SearchAreaBufferDegrees = 0.05;

    private readonly ITrailRepository repository;
    private readonly IElevationSource? elevation;

    /// <summary>
    /// Initializes a new route planner.
    /// </summary>
    /// <param name="repository">Trail repository used as the data source.</param>
    /// <param name="elevation">
    /// Optional terrain elevation source. When supplied, trail geometry is lifted onto the DEM before the graph
    /// is built so the time-cost (Tobler) sees real slopes; without it the graph is flat and "fastest time"
    /// degenerates to "shortest distance".
    /// </param>
    public TrailRoutePlanner(ITrailRepository repository, IElevationSource? elevation = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        this.repository = repository;
        this.elevation = elevation;
    }

    /// <inheritdoc />
    public async Task<Route?> PlanRouteAsync(RouteRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var searchBounds = ExpandBounds(request.Start, request.End, SearchAreaBufferDegrees);
        var trails = await repository.FindIntersectingAsync(searchBounds, cancellationToken).ConfigureAwait(false);
        if (trails.Count == 0)
        {
            return null;
        }

        if (elevation is not null)
        {
            trails = LiftOntoTerrain(trails, elevation);
        }

        // Full-geometry trails share junction nodes, so the 5 m snap connects them — no junction bridging hack.
        var graph = TrailGraph.Build(trails);
        var startNode = graph.FindNearestNode(request.Start);
        var goalNode = graph.FindNearestNode(request.End);
        if (startNode == NodeId.None || goalNode == NodeId.None)
        {
            return null;
        }

        IEdgeCostFunction costFunction = request.Profile switch
        {
            RouteProfile.FastestTime => new TimeCostFunction(),
            _ => new DistanceCostFunction(),
        };

        var router = new AStarRouter(graph);
        return router.FindPath(startNode, goalNode, costFunction);
    }

    /// <summary>
    /// Returns copies of the trails with each geometry point lifted to the sampled terrain elevation (points
    /// outside coverage keep their original, unset elevation). This is what lets the time-cost see real slopes.
    /// </summary>
    private static IReadOnlyList<Trail> LiftOntoTerrain(IReadOnlyList<Trail> trails, IElevationSource elevation)
    {
        var lifted = new List<Trail>(trails.Count);
        foreach (Trail trail in trails)
        {
            var geometry = new List<GeoPoint>(trail.Geometry.Count);
            foreach (GeoPoint point in trail.Geometry)
            {
                double? sampled = elevation.ElevationAt(point);
                geometry.Add(sampled is { } e ? new GeoPoint(point.Latitude, point.Longitude, e) : point);
            }

            lifted.Add(new Trail(trail.Id, trail.Name, trail.Markings, geometry));
        }

        return lifted;
    }

    private static MapBounds ExpandBounds(GeoPoint a, GeoPoint b, double bufferDegrees)
    {
        double minLat = Math.Min(a.Latitude, b.Latitude) - bufferDegrees;
        double maxLat = Math.Max(a.Latitude, b.Latitude) + bufferDegrees;
        double minLon = Math.Min(a.Longitude, b.Longitude) - bufferDegrees;
        double maxLon = Math.Max(a.Longitude, b.Longitude) + bufferDegrees;

        minLat = Math.Max(-90.0, minLat);
        maxLat = Math.Min(90.0, maxLat);
        minLon = Math.Max(-180.0, minLon);
        maxLon = Math.Min(180.0, maxLon);

        return new MapBounds(new GeoPoint(minLat, minLon), new GeoPoint(maxLat, maxLon));
    }
}