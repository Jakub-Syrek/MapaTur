using MapaTur.Domain.Geography;

namespace MapaTur.Application.Terrain;

/// <summary>
/// A small curated gazetteer of prominent Tatra summits (Polish and Slovak side), used by
/// <see cref="PeakNamer"/> to label DEM-detected <see cref="TerrainPeak"/>s. Coordinates are
/// approximate WGS84; matching tolerates the DEM grid offset, so a few hundred metres is fine.
/// Summits the gazetteer doesn't cover simply render with their elevation only.
/// </summary>
public static class TatraSummits
{
    /// <summary>Curated named summits, highest first.</summary>
    public static IReadOnlyList<NamedSummit> All { get; } = new[]
    {
        new NamedSummit("Gerlach", new GeoPoint(49.1641, 20.1340), 2655),
        new NamedSummit("Łomnica", new GeoPoint(49.1950, 20.2133), 2634),
        new NamedSummit("Lodowy Szczyt", new GeoPoint(49.1789, 20.1583), 2627),
        new NamedSummit("Wysoka", new GeoPoint(49.1772, 20.0997), 2547),
        new NamedSummit("Rysy", new GeoPoint(49.1795, 20.0882), 2501),
        new NamedSummit("Krywań", new GeoPoint(49.1625, 19.9994), 2495),
        new NamedSummit("Sławkowski Szczyt", new GeoPoint(49.1736, 20.2236), 2452),
        new NamedSummit("Mięguszowiecki Szczyt", new GeoPoint(49.1856, 20.0686), 2438),
        new NamedSummit("Świnica", new GeoPoint(49.2228, 19.9836), 2301),
        new NamedSummit("Kozi Wierch", new GeoPoint(49.2086, 20.0386), 2291),
        new NamedSummit("Bystra", new GeoPoint(49.1986, 19.8197), 2248),
        new NamedSummit("Granaty", new GeoPoint(49.2113, 20.0289), 2225),
        new NamedSummit("Starorobociański Wierch", new GeoPoint(49.2086, 19.8636), 2176),
        new NamedSummit("Małołączniak", new GeoPoint(49.2206, 19.9078), 2096),
        new NamedSummit("Mnich", new GeoPoint(49.1969, 20.0756), 2068),
        new NamedSummit("Wołowiec", new GeoPoint(49.2253, 19.8061), 2064),
        new NamedSummit("Kasprowy Wierch", new GeoPoint(49.2317, 19.9817), 1987),
        new NamedSummit("Giewont", new GeoPoint(49.2511, 19.9322), 1894),
        new NamedSummit("Czerwone Wierchy", new GeoPoint(49.2153, 19.9281), 2122),
        new NamedSummit("Wielka Kopa Koprowa", new GeoPoint(49.1853, 19.9697), 2052),
    };
}