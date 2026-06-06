using MapaTur.Domain.Geography;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Plans the moving detail window for roam-the-whole-Tatras LOD. A render mesh only holds ~3 km of 1 m
/// terrain (the vertex cap), so the streamer keeps a window of that size centred on where the camera looks
/// and reloads it (from the local tile cache — instant) as the camera flies. <see cref="Around"/> gives the
/// bounds to load for a focus point; <see cref="ShouldReload"/> debounces the reload so a small camera nudge
/// does not rebuild the mesh — it only fires once the focus has drifted past a threshold toward the edge.
/// </summary>
public static class LodTerrainWindow
{
    private const double MetersPerDegreeLat = 111_320.0;

    /// <summary>
    /// Square-ish bounds of half-extent <paramref name="halfWidthMeters"/> on each side of
    /// <paramref name="focus"/>. Longitude degrees are widened by 1/cos(lat) so the east–west ground span
    /// matches the north–south one at this latitude.
    /// </summary>
    public static MapBounds Around(GeoPoint focus, double halfWidthMeters)
    {
        double dLat = halfWidthMeters / MetersPerDegreeLat;
        double metersPerDegreeLon = MetersPerDegreeLat * Math.Cos(focus.Latitude * Math.PI / 180.0);
        double dLon = metersPerDegreeLon > 0.0 ? halfWidthMeters / metersPerDegreeLon : dLat;

        return new MapBounds(
            new GeoPoint(focus.Latitude - dLat, focus.Longitude - dLon),
            new GeoPoint(focus.Latitude + dLat, focus.Longitude + dLon));
    }

    /// <summary>
    /// True when <paramref name="focus"/> has moved more than <paramref name="thresholdMeters"/> from
    /// <paramref name="loadedCentre"/> (the centre the current window was loaded for) — i.e. the camera has
    /// drifted far enough that the detail window should be re-centred and reloaded.
    /// </summary>
    public static bool ShouldReload(GeoPoint loadedCentre, GeoPoint focus, double thresholdMeters)
        => DistanceMeters(loadedCentre, focus) > thresholdMeters;

    // Equirectangular approximation — accurate to well under a metre over the few-km scale this is used at,
    // and far cheaper than haversine for a per-frame-ish check.
    private static double DistanceMeters(GeoPoint a, GeoPoint b)
    {
        double meanLatRad = (a.Latitude + b.Latitude) / 2.0 * Math.PI / 180.0;
        double dLatMeters = (b.Latitude - a.Latitude) * MetersPerDegreeLat;
        double dLonMeters = (b.Longitude - a.Longitude) * MetersPerDegreeLat * Math.Cos(meanLatRad);
        return Math.Sqrt((dLatMeters * dLatMeters) + (dLonMeters * dLonMeters));
    }
}