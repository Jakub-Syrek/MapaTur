using MapaTur.Domain.Geography;
using MapaTur.Domain.Pois;

namespace MapaTur.Application.Pois;

/// <summary>
/// A small curated gazetteer of the well-known Tatra mountain huts (Polish schroniska + Slovak chaty),
/// always unioned into the searchable place picker so navigation targets like "Murowaniec" resolve offline
/// even when no POIs have been downloaded for the area (the downloaded OSM huts, when present, take
/// precedence and these are deduped out by name). Names carry the common searchable token; coordinates are
/// WGS84 decimal degrees (Wikipedia/OSM), accurate to a few hundred metres — fine for a camera teleport.
/// Negative ids mark these as curated so they never collide with real OSM node ids.
/// </summary>
public static class TatraHuts
{
    /// <summary>Curated named Tatra huts (Polish side first, then Slovak).</summary>
    public static IReadOnlyList<MountainPoi> All { get; } = new[]
    {
        // Polish side (PTTK schroniska).
        new MountainPoi(-1, "Murowaniec", new GeoPoint(49.243611, 20.006111), PoiKind.Hut, 1500),
        new MountainPoi(-2, "Schronisko w Dolinie Pięciu Stawów", new GeoPoint(49.201667, 20.038611), PoiKind.Hut, 1671),
        new MountainPoi(-3, "Schronisko nad Morskim Okiem", new GeoPoint(49.200833, 20.070556), PoiKind.Hut, 1410),
        new MountainPoi(-4, "Schronisko w Roztoce", new GeoPoint(49.224722, 20.046111), PoiKind.Hut, 1031),
        new MountainPoi(-5, "Schronisko na Polanie Chochołowskiej", new GeoPoint(49.223611, 19.788611), PoiKind.Hut, 1146),
        new MountainPoi(-6, "Schronisko na Hali Ornak", new GeoPoint(49.227500, 19.834722), PoiKind.Hut, 1100),
        new MountainPoi(-7, "Schronisko na Hali Kondratowej", new GeoPoint(49.253056, 19.936667), PoiKind.Hut, 1333),
        new MountainPoi(-8, "Schronisko na Kalatówkach", new GeoPoint(49.260556, 19.959444), PoiKind.Hut, 1198),

        // Slovak side (chaty).
        new MountainPoi(-9, "Téryho chata", new GeoPoint(49.195278, 20.182778), PoiKind.Hut, 2015),
        new MountainPoi(-10, "Zbojnícka chata", new GeoPoint(49.178056, 20.167500), PoiKind.Hut, 1960),
        new MountainPoi(-11, "Chata pri Zelenom plese", new GeoPoint(49.200278, 20.225556), PoiKind.Hut, 1551),
        new MountainPoi(-12, "Zamkovského chata", new GeoPoint(49.181667, 20.220556), PoiKind.Hut, 1475),
        new MountainPoi(-13, "Sliezsky dom", new GeoPoint(49.163333, 20.163889), PoiKind.Hut, 1670),
        new MountainPoi(-14, "Chata pri Popradskom plese", new GeoPoint(49.150278, 20.082778), PoiKind.Hut, 1494),
        new MountainPoi(-15, "Chata pod Rysmi", new GeoPoint(49.178889, 20.088333), PoiKind.Hut, 2250),
        new MountainPoi(-16, "Bilíkova chata", new GeoPoint(49.148611, 20.223611), PoiKind.Hut, 1255),
    };
}