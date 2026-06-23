using MapaTur.Domain.Geography;
using MapaTur.Domain.Pois;

namespace MapaTur.Application.Pois;

/// <summary>
/// A small curated gazetteer of the well-known Tatra mountain huts (Polish schroniska + Slovak chaty),
/// always unioned into the searchable place picker so navigation targets like "Murowaniec" resolve offline
/// even when no POIs have been downloaded for the area (the downloaded OSM huts, when present, take
/// precedence and these are deduped out by name). Names carry the common searchable token; coordinates are
/// WGS84 decimal degrees sourced from OSM (tourism=alpine_hut), accurate to the hut footprint.
/// Negative ids mark these as curated so they never collide with real OSM node ids.
/// </summary>
public static class TatraHuts
{
    /// <summary>Curated named Tatra huts (Polish side first, then Slovak).</summary>
    public static IReadOnlyList<MountainPoi> All { get; } = new[]
    {
        // Polish side (PTTK schroniska). Coordinates sourced from OSM tourism=alpine_hut (2026-06-23) — the
        // earlier hand values were off by up to ~3.7 km (Roztoka), which put markers on the wrong slope.
        new MountainPoi(-1, "Murowaniec", new GeoPoint(49.243418, 20.007160), PoiKind.Hut, 1500),
        new MountainPoi(-2, "Schronisko w Dolinie Pięciu Stawów", new GeoPoint(49.213641, 20.048740), PoiKind.Hut, 1671),
        new MountainPoi(-3, "Schronisko nad Morskim Okiem", new GeoPoint(49.201428, 20.071260), PoiKind.Hut, 1410),
        new MountainPoi(-4, "Schronisko w Roztoce", new GeoPoint(49.233720, 20.095677), PoiKind.Hut, 1031),
        new MountainPoi(-5, "Schronisko na Polanie Chochołowskiej", new GeoPoint(49.236347, 19.787759), PoiKind.Hut, 1146),
        new MountainPoi(-6, "Schronisko na Hali Ornak", new GeoPoint(49.229176, 19.858708), PoiKind.Hut, 1100),
        new MountainPoi(-7, "Schronisko na Hali Kondratowej", new GeoPoint(49.249882, 19.951682), PoiKind.Hut, 1333),
        new MountainPoi(-8, "Schronisko na Kalatówkach", new GeoPoint(49.260556, 19.959444), PoiKind.Hut, 1198),

        // Slovak side (chaty). Coordinates from OSM tourism=alpine_hut / hotel (Sliezsky dom).
        new MountainPoi(-9, "Téryho chata", new GeoPoint(49.190047, 20.198947), PoiKind.Hut, 2015),
        new MountainPoi(-10, "Zbojnícka chata", new GeoPoint(49.176609, 20.167395), PoiKind.Hut, 1960),
        new MountainPoi(-11, "Chata pri Zelenom plese", new GeoPoint(49.210135, 20.221076), PoiKind.Hut, 1551),
        new MountainPoi(-12, "Zamkovského chata", new GeoPoint(49.173992, 20.219717), PoiKind.Hut, 1475),
        new MountainPoi(-13, "Sliezsky dom", new GeoPoint(49.155997, 20.156527), PoiKind.Hut, 1670),
        new MountainPoi(-14, "Chata pri Popradskom plese", new GeoPoint(49.155013, 20.079908), PoiKind.Hut, 1494),
        new MountainPoi(-15, "Chata pod Rysmi", new GeoPoint(49.174604, 20.087028), PoiKind.Hut, 2250),
        new MountainPoi(-16, "Bilíkova chata", new GeoPoint(49.160049, 20.224360), PoiKind.Hut, 1255),
    };
}