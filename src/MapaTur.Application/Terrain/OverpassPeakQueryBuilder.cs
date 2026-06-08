using System.Globalization;

using MapaTur.Domain.Geography;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Builds the Overpass QL query that fetches every <c>natural=peak</c> within a bounding box. Mirrors the
/// POI/trails query convention: <c>[out:json]</c>, F6 invariant-culture bbox in south,west,north,east
/// order, and <c>out tags center</c> so ways/relations still return a representative point.
/// </summary>
public static class OverpassPeakQueryBuilder
{
    public static string BuildPeakQuery(MapBounds bounds, int timeoutSeconds = 60)
    {
        string south = bounds.SouthWest.Latitude.ToString("F6", CultureInfo.InvariantCulture);
        string west = bounds.SouthWest.Longitude.ToString("F6", CultureInfo.InvariantCulture);
        string north = bounds.NorthEast.Latitude.ToString("F6", CultureInfo.InvariantCulture);
        string east = bounds.NorthEast.Longitude.ToString("F6", CultureInfo.InvariantCulture);
        string bbox = $"{south},{west},{north},{east}";

        return $$"""
            [out:json][timeout:{{timeoutSeconds.ToString(CultureInfo.InvariantCulture)}}];
            (
              nwr["natural"="peak"]({{bbox}});
            );
            out tags center;
            """;
    }
}