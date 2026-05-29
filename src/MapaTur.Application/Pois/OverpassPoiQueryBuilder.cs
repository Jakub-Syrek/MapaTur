using System.Globalization;

using MapaTur.Domain.Geography;

namespace MapaTur.Application.Pois;

/// <summary>
/// Builds Overpass QL queries that fetch mountain POIs (huts, shelters, chalets, viewpoints) within a
/// bounding box. Picks up nodes, ways and relations carrying the relevant <c>tourism</c>/<c>amenity</c>
/// tags; <c>out tags center</c> gives a representative point per feature.
/// </summary>
public static class OverpassPoiQueryBuilder
{
    /// <summary>Builds a query selecting mountain POIs within the given bounding box.</summary>
    /// <param name="bounds">Geographic bounding box of interest.</param>
    /// <param name="timeoutSeconds">Per-request server-side timeout. Defaults to 60 seconds.</param>
    public static string BuildPoiQuery(MapBounds bounds, int timeoutSeconds = 60)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeoutSeconds, 1);

        string south = bounds.SouthWest.Latitude.ToString("F6", CultureInfo.InvariantCulture);
        string west = bounds.SouthWest.Longitude.ToString("F6", CultureInfo.InvariantCulture);
        string north = bounds.NorthEast.Latitude.ToString("F6", CultureInfo.InvariantCulture);
        string east = bounds.NorthEast.Longitude.ToString("F6", CultureInfo.InvariantCulture);
        string bbox = $"{south},{west},{north},{east}";

        return $$"""
            [out:json][timeout:{{timeoutSeconds.ToString(CultureInfo.InvariantCulture)}}];
            (
              nwr["tourism"="alpine_hut"]({{bbox}});
              nwr["tourism"="wilderness_hut"]({{bbox}});
              nwr["tourism"="chalet"]({{bbox}});
              nwr["tourism"="viewpoint"]({{bbox}});
              nwr["amenity"="shelter"]({{bbox}});
            );
            out tags center;
            """;
    }
}