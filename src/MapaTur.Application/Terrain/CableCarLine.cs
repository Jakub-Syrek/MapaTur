using MapaTur.Domain.Geography;

namespace MapaTur.Application.Terrain;

/// <summary>One aerialway station: a name, its geographic position and its elevation (m a.s.l.).</summary>
public sealed record CableCarStation(string Name, GeoPoint Location, double ElevationMeters);

/// <summary>
/// An aerialway line as an ordered list of stations; consecutive stations form the spans the cable runs
/// over (a transfer station appears once, shared by the spans on either side — e.g. Myślenickie Turnie).
/// </summary>
public sealed record CableCarLine(string Name, IReadOnlyList<CableCarStation> Stations);

/// <summary>
/// Hand-authored aerialway data. The Kasprowy Wierch cable car (PKL): Kuźnice → Myślenickie Turnie
/// (transfer) → Kasprowy Wierch, two spans. Coordinates/elevations are approximate (good enough for the
/// 3D overlay); a future upgrade can source the exact line + pylons from OSM `aerialway=*` via Overpass.
/// </summary>
public static class CableCarData
{
    /// <summary>The Kasprowy Wierch cable car: lower → transfer → upper.</summary>
    public static CableCarLine Kasprowy { get; } = new(
        "Kolej linowa na Kasprowy Wierch",
        new[]
        {
            new CableCarStation("Kuźnice", new GeoPoint(49.2766, 19.9836), 1027.0),
            new CableCarStation("Myślenickie Turnie", new GeoPoint(49.2607, 19.9794), 1352.0),
            new CableCarStation("Kasprowy Wierch", new GeoPoint(49.2316, 19.9817), 1959.0),
        });
}