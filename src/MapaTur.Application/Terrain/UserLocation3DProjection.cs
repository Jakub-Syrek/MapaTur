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
    /// <param name="bakedIndex">Optional baked z13-z16 tile index — when the fix has no GNSS altitude, seats the
    /// dot on the SAME real baked tile the route/trail lines use, so the dot stays glued to the route on steep
    /// ground instead of diverging from it. See <see cref="Trail3DWorldProjection.ToWorld"/>.</param>
    public static IReadOnlyList<MarkerWorldPoint<UserLocation>> ToWorld(
        IReadOnlyList<UserLocation> locations,
        DemRaster? raster,
        TerrainMesh3D mesh,
        float markerLiftMeters = 20f,
        BakedTileAvailabilityIndex? bakedIndex = null)
    {
        ArgumentNullException.ThrowIfNull(locations);
        ArgumentNullException.ThrowIfNull(mesh);

        if (locations.Count == 0)
        {
            return Array.Empty<MarkerWorldPoint<UserLocation>>();
        }

        var bakedTileCache = new Dictionary<(int Zoom, int X, int Y), DemRaster?>();
        var result = new List<MarkerWorldPoint<UserLocation>>(locations.Count);
        foreach (UserLocation fix in locations)
        {
            double lon = fix.Position.Longitude;
            double lat = fix.Position.Latitude;

            // GNSS-reported altitude wins when available — it's the truth on the ground for the user's
            // device. Fall back to the terrain only when the fix has no altitude (network/IP-based fixes,
            // older hardware, indoor positioning, or a synthetic route-preview position).
            // When falling back to terrain, seat on the SAME real baked tile the route/trail lines now use
            // (bakedIndex) so the dot stays glued to the route instead of diverging from it on steep ground —
            // the coarse base raster differs from the real baked surface by several metres there. Only where no
            // baked tile is loaded does it clamp to the base raster + flat fallback (never drop the marker).
            float groundElevation;
            if (fix.Position.ElevationMeters is { } gnssElevation)
            {
                groundElevation = (float)gnssElevation;
            }
            else if (bakedIndex is not null && Trail3DWorldProjection.TryGetBakedElevation(bakedIndex, bakedTileCache, fix.Position, out double bakedElevation))
            {
                groundElevation = (float)bakedElevation;
            }
            else
            {
                groundElevation = SampleGroundOrFallback(raster, lon, lat);
            }

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