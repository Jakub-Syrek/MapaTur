using MapaTur.Domain.Geography;

namespace MapaTur.Application.Terrain;

/// <summary>One documented perennial-firn site: a centre + reach the shader masks the firn term to.</summary>
/// <param name="Name">Site name (diagnostics only).</param>
/// <param name="Location">Approximate centre of the patch cluster.</param>
/// <param name="RadiusMeters">Reach of the site mask (feathered at the rim).</param>
/// <param name="MaxElevationMeters">Upper limit (m a.s.l.) of the tongues at this site: the deposition
/// zone is the LOWER couloirs + floor — the crags higher on the same wall (still inside the radius) are
/// bare rock, not dusted ("przyprószenie" bug: firn fired on concave nooks high on the Rysy wall).</param>
public readonly record struct FirnSite(string Name, GeoPoint Location, double RadiusMeters, double MaxElevationMeters);

/// <summary>
/// Curated gazetteer of the Tatra perennial-firn sites ("lodowczyki" / glacierets). The procedural firn
/// term (PerennialFirn: concavity + aspect + slope) makes PLAUSIBLE patches, but the real ones are a
/// short, well-documented list of topoclimatically lucky spots — so, like the lakes and summits, the
/// WHERE comes from data and the procedure only shapes the tongue INSIDE each site. Coordinates are
/// approximate cluster centres (±~200 m) with generous radii; tune against photos.
/// </summary>
public static class FirnSiteData
{
    /// <summary>The documented sites, Polish and Slovak side of the High Tatras core.</summary>
    public static readonly IReadOnlyList<FirnSite> Sites =
    [
        // The Mięguszowiecki group — the classic Polish glacierets.
        new("Mięguszowiecki Kocioł", new GeoPoint(49.1866, 20.0650), 420, 2150),
        new("Bandzioch Mięguszowiecki", new GeoPoint(49.1897, 20.0618), 380, 2200),
        new("Mały Kocioł Mięguszowiecki", new GeoPoint(49.1886, 20.0706), 260, 2100),
        // The couloirs and floor above Czarny Staw pod Rysami (the photo-reference tongues). Anchored to
        // the LAKE GAZETTEER geometry (centroid 49.18836, 20.07652; south shore 49.18592) — the first cut
        // sat ~600 m too far east and painted the sunlit ridge flank instead of the runouts over the water.
        new("Kocioł pod Rysami", new GeoPoint(49.1830, 20.0768), 430, 2000),
        new("Żleby Niżnich Rysów", new GeoPoint(49.1846, 20.0812), 300, 2050),
        // Slovak side below Waga / the Rysy SW bowl.
        new("Kotlina pod Wagą", new GeoPoint(49.1758, 20.0855), 320, 2150),
        // Orla Perć north cirques.
        new("Kozia Dolinka", new GeoPoint(49.2243, 20.0157), 360, 2200),
        new("Dolinka Pusta", new GeoPoint(49.2216, 20.0262), 320, 2200),
        new("Dolinka pod Kołem", new GeoPoint(49.2179, 20.0056), 320, 2200),
    ];
}