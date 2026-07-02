using System.Globalization;

using MapaTur.Domain.Geography;

namespace MapaTur.Application.Waterways;

/// <summary>
/// Builds Overpass QL queries that fetch surface watercourses within a bbox: <c>waterway=river|stream</c>
/// ways (the mountain streams and their receiving rivers) plus <c>waterway=waterfall</c> nodes (named falls
/// like Siklawa / Wodogrzmoty Mickiewicza). Ditches/drains/canals are excluded — they are lowland
/// infrastructure, not the "strumyki i wodospady" of a hiking map.
/// </summary>
public static class OverpassWaterwayQueryBuilder
{
    private const string WaterwayClasses = "river|stream";

    /// <summary>
    /// Builds a query selecting watercourse ways (geometry inline) and waterfall nodes within the box.
    /// </summary>
    /// <param name="bounds">Geographic bounding box of interest.</param>
    /// <param name="timeoutSeconds">Per-request server-side timeout. Defaults to 60 seconds.</param>
    public static string BuildWaterwaysQuery(MapBounds bounds, int timeoutSeconds = 60)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeoutSeconds, 1);

        string south = bounds.SouthWest.Latitude.ToString("F6", CultureInfo.InvariantCulture);
        string west = bounds.SouthWest.Longitude.ToString("F6", CultureInfo.InvariantCulture);
        string north = bounds.NorthEast.Latitude.ToString("F6", CultureInfo.InvariantCulture);
        string east = bounds.NorthEast.Longitude.ToString("F6", CultureInfo.InvariantCulture);

        return $$"""
            [out:json][timeout:{{timeoutSeconds.ToString(CultureInfo.InvariantCulture)}}];
            (
              way["waterway"~"^({{WaterwayClasses}})$"]({{south}},{{west}},{{north}},{{east}});
              node["waterway"="waterfall"]({{south}},{{west}},{{north}},{{east}});
            );
            out geom;
            """;
    }
}