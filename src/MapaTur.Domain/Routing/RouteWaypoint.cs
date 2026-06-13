using MapaTur.Domain.Geography;

namespace MapaTur.Domain.Routing;

/// <summary>What a route stop refers to — drives the list glyph and lets the UI group the picker.</summary>
public enum WaypointKind
{
    /// <summary>A named summit (from the OSM peak gazetteer).</summary>
    Peak = 0,

    /// <summary>A hut, shelter, chalet or viewpoint (mountain POI).</summary>
    Hut = 1,

    /// <summary>A named lake / tarn.</summary>
    Lake = 2,

    /// <summary>A bare point on the terrain (a free tap that snapped to the nearest trail).</summary>
    TrailPoint = 3,
}

/// <summary>
/// One stop in a tourist-map route chain: a named place (or a tapped trail point) the walker wants to
/// pass through. The route planner only uses <see cref="Location"/>; the name + kind are for the list
/// and the on-map label.
/// </summary>
/// <param name="Name">Display name ("Rysy", "Schronisko nad Morskim Okiem", or "Punkt na szlaku").</param>
/// <param name="Location">Geographic position used for planning.</param>
/// <param name="Kind">Category (peak / hut / lake / tapped trail point).</param>
/// <param name="ElevationMeters">Elevation in metres when known; null otherwise.</param>
public sealed record RouteWaypoint(string Name, GeoPoint Location, WaypointKind Kind, double? ElevationMeters = null);