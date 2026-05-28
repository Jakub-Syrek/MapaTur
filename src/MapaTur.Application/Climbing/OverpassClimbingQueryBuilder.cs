using System.Globalization;

using MapaTur.Domain.Geography;

namespace MapaTur.Application.Climbing;

/// <summary>
/// Builds Overpass QL queries that fetch climbing-tagged OSM features (nodes, ways,
/// relations) within a bounding box. We pick up anything carrying the <c>sport=climbing</c>
/// or <c>climbing=*</c> tags, plus annotated cliffs.
/// </summary>
public static class OverpassClimbingQueryBuilder
{
    /// <summary>
    /// Builds a query selecting climbing features within the given bounding box.
    /// </summary>
    /// <param name="bounds">Geographic bounding box of interest.</param>
    /// <param name="timeoutSeconds">Per-request server-side timeout. Defaults to 60 seconds.</param>
    /// <returns>An Overpass QL query string ready to POST.</returns>
    public static string BuildClimbingQuery(MapBounds bounds, int timeoutSeconds = 60)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeoutSeconds, 1);

        string south = bounds.SouthWest.Latitude.ToString("F6", CultureInfo.InvariantCulture);
        string west = bounds.SouthWest.Longitude.ToString("F6", CultureInfo.InvariantCulture);
        string north = bounds.NorthEast.Latitude.ToString("F6", CultureInfo.InvariantCulture);
        string east = bounds.NorthEast.Longitude.ToString("F6", CultureInfo.InvariantCulture);

        return $$"""
            [out:json][timeout:{{timeoutSeconds.ToString(CultureInfo.InvariantCulture)}}];
            (
              nwr["sport"="climbing"]({{south}},{{west}},{{north}},{{east}});
              nwr["climbing"]({{south}},{{west}},{{north}},{{east}});
              way["natural"="cliff"]["climbing"]({{south}},{{west}},{{north}},{{east}});
            );
            out tags center;
            """;
    }
}