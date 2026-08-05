using MapaTur.Domain.Geography;
using MapaTur.Domain.Pois;

namespace MapaTur.Application.Pois;

/// <summary>
/// Curated gazetteer of the trailhead CAR PARKS a walk actually starts and ends at, modelled as
/// <see cref="PoiKind.Parking"/> POIs so they are searchable and drawn OFFLINE, without a POI download —
/// same contract as <see cref="TatraHuts"/> and <see cref="TatraPasses"/>. A live "Pobierz POI (widok)"
/// still wins (deduped by name, or by proximity for the same kind, in <see cref="PoiMerger"/>).
/// <para>
/// Why this list exists (2026-08-05): planning a Roháče route from Zverovka had no start point to pick.
/// The cached POI set for that box (49.15-49.32, 19.58-19.88) holds 19 entries, ALL unnamed, and not one
/// <see cref="PoiKind.Parking"/> — the download had only ever covered the Polish footprint.
/// </para>
/// Coordinates are WGS84 from OSM (<c>amenity=parking</c>); elevations are sampled from OUR z16 DEM at the
/// node, so they agree with the terrain the marker is seated on rather than with a web page.
/// Negative ids mark these as curated (huts hold -1..-16, passes -100..-210, parkings start at -300) so
/// they never collide with real OSM node ids.
/// </summary>
public static class TatraTrailheadParking
{
    /// <summary>Curated trailhead car parks.</summary>
    public static IReadOnlyList<MountainPoi> All { get; } = new[]
    {
        // Roháče / Západné Tatry. The ONLY named amenity=parking in the whole massif (Overpass sweep of
        // 49.17-49.30, 19.62-19.85 on 2026-08-05 returned 30 car parks, 29 of them nameless): the paid
        // surface car park at Zverovka, below Spálená (2083 m) and Predný Salatín — the trailhead for
        // Ťatliakovo jazero, Baníkov and the Rohacze ridge. DEM sample at the node: 1031 m.
        new MountainPoi(-300, "Parking Zverovka – Spálená", new GeoPoint(49.238710, 19.714070), PoiKind.Parking, 1031),
    };
}
