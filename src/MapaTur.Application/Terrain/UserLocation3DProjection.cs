using System.Numerics;

using MapaTur.Domain.Location;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Lifts a single <see cref="UserLocation"/> GPS fix onto the 3D terrain. Prefers the OS-reported
/// GNSS altitude (<see cref="Domain.Geography.GeoPoint.ElevationMeters"/>) when present — it is
/// already in real-world metres above mean sea level — and falls back to a DEM bilinear lookup
/// when the fix has no altitude (e.g. a network-based fix or a synthetic position used in tests).
/// The marker sits a few metres above the ground so the dot stays visible instead of z-fighting
/// the mesh on flat sections.
/// </summary>
public static class UserLocation3DProjection
{
    /// <summary>
    /// Builds the camera-independent world-space anchor for the given fix, ready to be screen-projected
    /// by <see cref="Marker3DOverlayProjector{TSource, TProjected}"/>. Returns an empty list only when the
    /// input list is empty — a fix is always seated (GNSS altitude → clamped DEM sample → flat fallback) so
    /// the dot never silently vanishes outside the mesh or over a NoData hole.
    /// </summary>
    /// <param name="locations">Single-element list (current fix) or empty.</param>
    /// <param name="raster">DEM used to look up ground elevation when the fix has no GNSS altitude.</param>
    /// <param name="mesh">Mesh whose world-space convention defines the coordinate system.</param>
    /// <param name="markerLiftMeters">Vertical offset above the ground so the marker sits clear of the surface.</param>
    public static IReadOnlyList<MarkerWorldPoint<UserLocation>> ToWorld(
        IReadOnlyList<UserLocation> locations,
        DemRaster? raster,
        TerrainMesh3D mesh,
        float markerLiftMeters = 20f)
    {
        ArgumentNullException.ThrowIfNull(locations);
        ArgumentNullException.ThrowIfNull(mesh);

        if (locations.Count == 0)
        {
            return Array.Empty<MarkerWorldPoint<UserLocation>>();
        }

        var result = new List<MarkerWorldPoint<UserLocation>>(locations.Count);
        foreach (UserLocation fix in locations)
        {
            double lon = fix.Position.Longitude;
            double lat = fix.Position.Latitude;

            // GNSS-reported altitude wins when available — it's the truth on the ground for the user's
            // device. Fall back to the DEM only when the fix has no altitude (network/IP-based fixes,
            // older hardware, indoor positioning).
            // GNSS altitude wins when present; otherwise seat the dot on the DEM. We CLAMP the lookup to the
            // loaded raster and fall back to a flat default rather than dropping the marker — a fix just
            // outside the mesh or over a NoData hole used to vanish, which read in the field as "raz punkt
            // jest, raz nie". The dot is drawn occlusion-free, so an approximate height only nudges its screen
            // position; losing it entirely is far worse than a slightly-off elevation.
            float groundElevation = fix.Position.ElevationMeters is { } gnssElevation
                ? (float)gnssElevation
                : SampleGroundOrFallback(raster, lon, lat);

            Vector3 world = mesh.GeoToWorld(fix.Position, groundElevation + markerLiftMeters);
            result.Add(new MarkerWorldPoint<UserLocation>(fix, world));
        }

        return result;
    }

    private const float FallbackGroundElevationMeters = 1000f; // ~a Tatra valley floor — last resort, no DEM

    private static float SampleGroundOrFallback(DemRaster? raster, double lon, double lat)
    {
        if (raster is null)
        {
            return FallbackGroundElevationMeters;
        }

        double clampedLon = Math.Clamp(lon, raster.West, raster.East);
        double clampedLat = Math.Clamp(lat, raster.South, raster.North);
        double sample = raster.SampleBilinear(clampedLon, clampedLat);
        return sample > raster.NoDataValue ? (float)sample : FallbackGroundElevationMeters;
    }
}

/// <summary>
/// A user-location fix projected onto the 3D viewport. <see cref="ScreenPosition"/> is pixel
/// coordinates + NDC depth (X, Y, Z) when in-frustum, or null when behind the camera / clipped.
/// </summary>
/// <param name="Source">Originating <see cref="UserLocation"/> — renderers read accuracy/timestamp from here.</param>
/// <param name="ScreenPosition">Screen position; null when off-frustum.</param>
public readonly record struct ProjectedUserLocation(UserLocation Source, Vector3? ScreenPosition);