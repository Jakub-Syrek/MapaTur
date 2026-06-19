using System.Text.Json;

using MapaTur.Domain.Geography;
using MapaTur.Domain.Routing;

namespace MapaTur.Application.Routing;

/// <summary>
/// Serializes a planned route's stops to/from a compact JSON string so the tourist route survives an app
/// restart. Round-trips name + lat/lon + kind + elevation; a null, empty or corrupt payload yields no stops.
/// </summary>
public static class RouteStopsSerializer
{
    private sealed record Dto(string Name, double Lat, double Lon, int Kind, double? Elev);

    /// <summary>Serializes the ordered stops to JSON. Throws if <paramref name="stops"/> is null.</summary>
    public static string Serialize(IReadOnlyList<RouteWaypoint> stops)
    {
        ArgumentNullException.ThrowIfNull(stops);

        var dtos = new List<Dto>(stops.Count);
        foreach (RouteWaypoint s in stops)
        {
            dtos.Add(new Dto(s.Name, s.Location.Latitude, s.Location.Longitude, (int)s.Kind, s.ElevationMeters));
        }

        return JsonSerializer.Serialize(dtos);
    }

    /// <summary>Reads stops back. A null, empty or corrupt payload returns an empty list (never throws).</summary>
    public static IReadOnlyList<RouteWaypoint> Deserialize(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return Array.Empty<RouteWaypoint>();
        }

        List<Dto>? dtos;
        try
        {
            dtos = JsonSerializer.Deserialize<List<Dto>>(json);
        }
        catch (JsonException)
        {
            return Array.Empty<RouteWaypoint>();
        }

        if (dtos is null)
        {
            return Array.Empty<RouteWaypoint>();
        }

        var result = new List<RouteWaypoint>(dtos.Count);
        foreach (Dto d in dtos)
        {
            result.Add(new RouteWaypoint(d.Name, new GeoPoint(d.Lat, d.Lon), (WaypointKind)d.Kind, d.Elev));
        }

        return result;
    }
}