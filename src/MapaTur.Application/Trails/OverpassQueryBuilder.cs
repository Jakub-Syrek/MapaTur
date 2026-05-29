using System.Globalization;

using MapaTur.Domain.Geography;

namespace MapaTur.Application.Trails;

/// <summary>
/// Builds Overpass QL queries that fetch all hiking-route relations whose members
/// intersect a given bounding box.
/// </summary>
public static class OverpassQueryBuilder
{
    /// <summary>
    /// Builds a query selecting <c>route=hiking</c> relations and their member ways
    /// within the given bounding box, returning geometry inline so the response is
    /// self-contained.
    /// </summary>
    /// <param name="bounds">Geographic bounding box of interest.</param>
    /// <param name="timeoutSeconds">Per-request server-side timeout. Defaults to 60 seconds.</param>
    /// <returns>An Overpass QL query string ready to be POSTed to a public endpoint.</returns>
    public static string BuildHikingTrailsQuery(MapBounds bounds, int timeoutSeconds = 60)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeoutSeconds, 1);

        string south = bounds.SouthWest.Latitude.ToString("F6", CultureInfo.InvariantCulture);
        string west = bounds.SouthWest.Longitude.ToString("F6", CultureInfo.InvariantCulture);
        string north = bounds.NorthEast.Latitude.ToString("F6", CultureInfo.InvariantCulture);
        string east = bounds.NorthEast.Longitude.ToString("F6", CultureInfo.InvariantCulture);

        return $$"""
            [out:json][timeout:{{timeoutSeconds.ToString(CultureInfo.InvariantCulture)}}];
            (
              relation["route"="hiking"]({{south}},{{west}},{{north}},{{east}});
            );
            out body;
            >;
            out skel geom;
            """;
    }
}