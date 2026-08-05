using MapaTur.Application.Terrain;
using MapaTur.Application.Tracks;
using MapaTur.Application.Trails;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Routing;
using MapaTur.Domain.Tracks;
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
    private readonly ITrackRepository? offTrailTracks;
    private readonly ITrailRepository? unmarkedPaths;

    /// <summary>
    /// Initializes a new route planner.
    /// </summary>
    /// <param name="repository">Trail repository used as the data source.</param>
    /// <param name="elevation">
    /// Optional terrain elevation source. When supplied, trail geometry is lifted onto the DEM before the graph
    /// is built so the time-cost (Tobler) sees real slopes; without it the graph is flat and "fastest time"
    /// degenerates to "shortest distance".
    /// </param>
    /// <param name="offTrailTracks">
    /// Optional store of user-imported off-trail ("pozaszlaki") tracks. Only consulted when the request opts in
    /// via <see cref="RouteRequest.IncludeOffTrailTracks"/>; null disables off-trail routing entirely.
    /// </param>
    /// <param name="unmarkedPaths">
    /// Optional store of UNMARKED OSM paths (perci/ways bez koloru i bez relacji szlaku — 2026-08-05, Rohacze:
    /// zejście z Nohavicy i łącznik na Zadną Zábrať istnieją w OSM tylko w tej postaci). Consulted under the
    /// SAME opt-in flag as the user's tracks, so default planning stays byte-identical on marked trails.
    /// </param>
    public TrailRoutePlanner(
        ITrailRepository repository,
        IElevationSource? elevation = null,
        ITrackRepository? offTrailTracks = null,
        ITrailRepository? unmarkedPaths = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        this.repository = repository;
        this.elevation = elevation;
        this.offTrailTracks = offTrailTracks;
        this.unmarkedPaths = unmarkedPaths;
    }

    /// <inheritdoc />
    public async Task<Route?> PlanRouteAsync(RouteRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var searchBounds = ExpandBounds(request.Start, request.End, SearchAreaBufferDegrees);
        var trails = await repository.FindIntersectingAsync(searchBounds, cancellationToken).ConfigureAwait(false);

        // Off-trail ("pozaszlaki") tracks are only pulled in when the request opts in — otherwise the on-trail
        // graph (and the whole descent-routing behaviour it encodes) is byte-identical to before.
        IReadOnlyList<Trail> offTrail = Array.Empty<Trail>();
        if (request.IncludeOffTrailTracks && offTrailTracks is not null)
        {
            offTrail = await LoadOffTrailTrailsAsync(searchBounds, cancellationToken).ConfigureAwait(false);
        }

        // Nieznakowane ścieżki OSM wchodzą pod TĘ SAMĄ flagę i z TĄ SAMĄ karą kosztu co ślady usera —
        // graf szlaków znakowanych zostaje nietknięty, dopóki user świadomie nie włączy pozaszlaków.
        if (request.IncludeOffTrailTracks && unmarkedPaths is not null)
        {
            IReadOnlyList<Trail> paths = await unmarkedPaths.FindIntersectingAsync(searchBounds, cancellationToken).ConfigureAwait(false);
            if (paths.Count > 0)
            {
                offTrail = offTrail.Count == 0 ? paths : offTrail.Concat(paths).ToList();
            }
        }

        if (trails.Count == 0 && offTrail.Count == 0)
        {
            return null;
        }

        if (elevation is not null)
        {
            trails = LiftOntoTerrain(trails, elevation);
            offTrail = LiftOntoTerrain(offTrail, elevation);
        }

        // Full-geometry trails share junction nodes, so the 5 m snap connects them — no junction bridging hack.
        // Off-trail tracks snap onto trail nodes the same way, adding penalised off-trail edges.
        var graph = TrailGraph.Build(trails, offTrail);
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

    /// <summary>
    /// Loads the user's off-trail tracks, keeps those whose bounding box overlaps the search area, and converts
    /// each to a <see cref="Trail"/> polyline (empty markings — the off-trail flag is applied by the graph, not
    /// the colour). Tracks with fewer than two points can't form an edge and are dropped.
    /// </summary>
    private async Task<IReadOnlyList<Trail>> LoadOffTrailTrailsAsync(MapBounds bounds, CancellationToken cancellationToken)
    {
        var tracks = await offTrailTracks!.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<Trail>(tracks.Count);
        foreach (Track track in tracks)
        {
            if (!OverlapsBounds(track, bounds))
            {
                continue;
            }

            var geometry = new List<GeoPoint>(track.Points.Count);
            foreach (TrackPoint point in track.Points)
            {
                geometry.Add(point.Position);
            }

            if (geometry.Count >= 2)
            {
                result.Add(new Trail(unchecked((long)track.Id.GetHashCode()), track.Name, Array.Empty<TrailMarking>(), geometry));
            }
        }

        return result;
    }

    private static bool OverlapsBounds(Track track, MapBounds bounds)
    {
        double minLat = track.Points[0].Position.Latitude;
        double maxLat = minLat;
        double minLon = track.Points[0].Position.Longitude;
        double maxLon = minLon;

        foreach (TrackPoint point in track.Points)
        {
            double lat = point.Position.Latitude;
            double lon = point.Position.Longitude;
            if (lat < minLat) minLat = lat;
            if (lat > maxLat) maxLat = lat;
            if (lon < minLon) minLon = lon;
            if (lon > maxLon) maxLon = lon;
        }

        return !(maxLat < bounds.SouthWest.Latitude
            || minLat > bounds.NorthEast.Latitude
            || maxLon < bounds.SouthWest.Longitude
            || minLon > bounds.NorthEast.Longitude);
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