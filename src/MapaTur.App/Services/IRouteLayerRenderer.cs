using MapaTur.Domain.Geography;
using MapaTur.Domain.Routing;
using Map = Mapsui.Map;

namespace MapaTur.App.Services;

/// <summary>
/// Renders a planned <see cref="Route"/> and its endpoint waypoints on a Mapsui map.
/// </summary>
public interface IRouteLayerRenderer
{
    /// <summary>
    /// Draws the route polyline on the map. Replaces any previously drawn route layer.
    /// </summary>
    /// <param name="map">The map to mutate.</param>
    /// <param name="route">Planned route to draw.</param>
    void RenderRoute(Map map, Route route);

    /// <summary>
    /// Draws the user-picked waypoints (start, optional end) as visible markers.
    /// Replaces any previously drawn waypoint layer.
    /// </summary>
    /// <param name="map">The map to mutate.</param>
    /// <param name="waypoints">Waypoints to draw.</param>
    void RenderWaypoints(Map map, IReadOnlyList<GeoPoint> waypoints);

    /// <summary>
    /// Removes both the route and waypoint layers, if present.
    /// </summary>
    /// <param name="map">The map to mutate.</param>
    void Clear(Map map);
}
