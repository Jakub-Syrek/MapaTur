using System.Globalization;

using MapaTur.Domain.Geography;

namespace MapaTur.Application.Roads;

/// <summary>
/// Builds Overpass QL queries that fetch drivable/vehicular roads (<c>highway=*</c> ways) within a bbox.
/// Foot/path/steps are intentionally excluded — those overlap the hiking-trail layer.
/// </summary>
public static class OverpassRoadQueryBuilder
{
    // Vehicular road classes; excludes footway/path/steps/cycleway (covered by trails or not "roads").
    private const string HighwayClasses =
        "motorway|trunk|primary|secondary|tertiary|unclassified|residential|living_street|service|track";

    /// <summary>
    /// Builds a query selecting road ways within the bounding box, returning geometry inline so the
    /// response is self-contained (one way → one polyline).
    /// </summary>
    /// <param name="bounds">Geographic bounding box of interest.</param>
    /// <param name="timeoutSeconds">Per-request server-side timeout. Defaults to 60 seconds.</param>
    public static string BuildRoadsQuery(MapBounds bounds, int timeoutSeconds = 60)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeoutSeconds, 1);

        string south = bounds.SouthWest.Latitude.ToString("F6", CultureInfo.InvariantCulture);
        string west = bounds.SouthWest.Longitude.ToString("F6", CultureInfo.InvariantCulture);
        string north = bounds.NorthEast.Latitude.ToString("F6", CultureInfo.InvariantCulture);
        string east = bounds.NorthEast.Longitude.ToString("F6", CultureInfo.InvariantCulture);

        return $$"""
            [out:json][timeout:{{timeoutSeconds.ToString(CultureInfo.InvariantCulture)}}];
            (
              way["highway"~"^({{HighwayClasses}})$"]({{south}},{{west}},{{north}},{{east}});
            );
            out geom;
            """;
    }
}