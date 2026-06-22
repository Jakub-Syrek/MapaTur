using System.Globalization;

using MapaTur.Domain.Geography;

namespace MapaTur.Application.Trails;

/// <summary>
/// Builds Overpass QL queries for "exposed" mountain routes — the demanding/secured paths a guide takes
/// rather than ordinary marked trails: ways tagged <c>sac_scale</c> at demanding/alpine grades or any
/// <c>via_ferrata</c>. These are returned with inline geometry (<c>out geom</c>) as standalone ways (not
/// relations), so the renderer can draw them as a dotted overlay distinct from the normal hiking lines.
/// </summary>
public static class OverpassExposedRouteQueryBuilder
{
    /// <summary>
    /// Builds a query selecting exposed/secured route ways within the bounding box, geometry inline.
    /// </summary>
    /// <param name="bounds">Geographic bounding box of interest.</param>
    /// <param name="timeoutSeconds">Per-request server-side timeout. Defaults to 60 seconds.</param>
    public static string BuildExposedRoutesQuery(MapBounds bounds, int timeoutSeconds = 60)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeoutSeconds, 1);

        string south = bounds.SouthWest.Latitude.ToString("F6", CultureInfo.InvariantCulture);
        string west = bounds.SouthWest.Longitude.ToString("F6", CultureInfo.InvariantCulture);
        string north = bounds.NorthEast.Latitude.ToString("F6", CultureInfo.InvariantCulture);
        string east = bounds.NorthEast.Longitude.ToString("F6", CultureInfo.InvariantCulture);

        // The "guide / off-marked" routes the way mapa-turystyczna.pl dots them (OSM, ODbL):
        //  • sac_scale ~ demanding|alpine — grades above ordinary marked trails (Orla Perć etc.);
        //  • via_ferrata — secured routes;
        //  • highway=path with trail_visibility bad|horrible|no OR informal=yes — unmarked "perci" / guide
        //    paths that carry no marked-route relation. Together these match the dotted exposed/guide network.
        return $$"""
            [out:json][timeout:{{timeoutSeconds.ToString(CultureInfo.InvariantCulture)}}];
            (
              way["sac_scale"~"demanding|alpine"]({{south}},{{west}},{{north}},{{east}});
              way["highway"="via_ferrata"]({{south}},{{west}},{{north}},{{east}});
              way["via_ferrata_scale"]({{south}},{{west}},{{north}},{{east}});
              way["highway"="path"]["trail_visibility"~"bad|horrible|no"]({{south}},{{west}},{{north}},{{east}});
              way["highway"="path"]["informal"="yes"]({{south}},{{west}},{{north}},{{east}});
            );
            out geom;
            """;
    }
}