using MapaTur.Domain.Geography;

namespace MapaTur.Application.Terrain;

/// <summary>
/// A small curated gazetteer of prominent Tatra summits (Polish and Slovak side), used by
/// <see cref="PeakNamer"/> to label DEM-detected <see cref="TerrainPeak"/>s. Coordinates are WGS84
/// decimal degrees taken from each summit's Wikipedia infobox; <see cref="PeakNamer.MergeWithGazetteer"/>
/// snaps each to the nearest DEM maximum, so they need to be accurate to a few hundred metres.
/// Summits the gazetteer doesn't cover simply render with their elevation only.
/// </summary>
public static class TatraSummits
{
    /// <summary>Curated named summits, highest first.</summary>
    public static IReadOnlyList<NamedSummit> All { get; } = new[]
    {
        new NamedSummit("Gerlach", new GeoPoint(49.164028, 20.134028), 2655),
        new NamedSummit("Łomnica", new GeoPoint(49.195139, 20.213056), 2634),
        new NamedSummit("Lodowy Szczyt", new GeoPoint(49.198556, 20.182750), 2627),
        new NamedSummit("Wysoka", new GeoPoint(49.172611, 20.094167), 2547),
        new NamedSummit("Rysy", new GeoPoint(49.179306, 20.088444), 2501),
        new NamedSummit("Krywań", new GeoPoint(49.162833, 20.000056), 2495),
        new NamedSummit("Sławkowski Szczyt", new GeoPoint(49.166083, 20.184667), 2452),
        new NamedSummit("Mięguszowiecki Szczyt", new GeoPoint(49.187028, 20.059333), 2438),
        new NamedSummit("Świnica", new GeoPoint(49.219417, 20.009306), 2301),
        new NamedSummit("Kozi Wierch", new GeoPoint(49.218300, 20.028600), 2291),
        new NamedSummit("Bystra", new GeoPoint(49.188639, 19.842583), 2248),
        new NamedSummit("Granaty", new GeoPoint(49.226944, 20.033306), 2225),
        new NamedSummit("Starorobociański Wierch", new GeoPoint(49.199361, 19.819944), 2176),
        new NamedSummit("Czerwone Wierchy", new GeoPoint(49.231667, 19.909472), 2122),
        new NamedSummit("Małołączniak", new GeoPoint(49.235806, 19.919306), 2096),
        new NamedSummit("Mnich", new GeoPoint(49.192500, 20.055000), 2068),
        new NamedSummit("Wołowiec", new GeoPoint(49.207556, 19.763111), 2064),
        new NamedSummit("Wielka Kopa Koprowa", new GeoPoint(49.200667, 19.973889), 2052),
        new NamedSummit("Kasprowy Wierch", new GeoPoint(49.231833, 19.981556), 1987),
        new NamedSummit("Giewont", new GeoPoint(49.250944, 19.934139), 1894),
    };
}