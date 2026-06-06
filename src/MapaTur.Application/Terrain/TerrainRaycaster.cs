using System.Numerics;

using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Intersects a world-space <see cref="Ray"/> with a DEM heightfield and returns the first
/// surface hit, or null when the ray meets no real terrain. This is the geometric core of
/// the screen-space-error LOD's look-at point (Krok 1): cast a ray and find what it hits,
/// possibly kilometres away, rather than assuming the camera target is on the ground.
///
/// The march samples terrain elevation by mapping each world point back to geographic
/// coordinates (<see cref="LocalTangentProjection.WorldToGeo"/>) and bilinearly sampling the
/// raster. Points outside the raster bounds or over <see cref="DemRaster.NoDataValue"/> are
/// treated as gaps with no surface — <see cref="DemRaster.SampleBilinear"/> clamps out-of-bounds
/// coordinates to the edge, so the bounds check here is what stops the clamped edge value from
/// being mistaken for a hit.
/// </summary>
public static class TerrainRaycaster
{
    private const int BisectionIterations = 30;

    /// <summary>
    /// Marches <paramref name="ray"/> through the DEM and returns the first world-space point where it
    /// crosses below the terrain surface, refined by bisection. Returns null if the ray never crosses a
    /// real surface within <paramref name="maxDistanceMeters"/>, or if it starts at/below the surface.
    /// </summary>
    /// <param name="ray">Ray in the metric world frame (X east, Y north, Z up).</param>
    /// <param name="raster">DEM heightfield to intersect.</param>
    /// <param name="anchor">Shared projection origin tying the world frame to geographic coordinates.</param>
    /// <param name="verticalExaggeration">Z scale applied to raw elevations, matching the rendered terrain.</param>
    /// <param name="maxDistanceMeters">How far along the ray to march before giving up.</param>
    /// <param name="stepMeters">March step length. Smaller resolves thin ridges but costs more samples.</param>
    public static Vector3? Intersect(
        Ray ray,
        DemRaster raster,
        GeoPoint anchor,
        float verticalExaggeration,
        float maxDistanceMeters,
        float stepMeters)
    {
        ArgumentNullException.ThrowIfNull(raster);
        if (stepMeters <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(stepMeters), stepMeters, "stepMeters must be positive.");
        }

        if (maxDistanceMeters <= 0f)
        {
            return null;
        }

        if (ray.Direction == Vector3.Zero)
        {
            return null;
        }

        Vector3 direction = Vector3.Normalize(ray.Direction);

        // Height-above-surface at the origin. A null means "no real terrain here" (out of bounds / no-data).
        float? originSurface = SurfaceZ(ray.Origin, raster, anchor, verticalExaggeration);
        if (originSurface is float os && ray.Origin.Z - os <= 0f)
        {
            // The ray starts at or under the surface — no forward crossing to report.
            return null;
        }

        float previousT = 0f;
        float? previousGap = originSurface is float originZ ? ray.Origin.Z - originZ : null; // null over a gap

        int steps = (int)MathF.Ceiling(maxDistanceMeters / stepMeters);
        float t = 0f;
        for (int i = 0; i < steps; i++)
        {
            t = MathF.Min(t + stepMeters, maxDistanceMeters);
            Vector3 point = ray.Origin + (direction * t);
            float? surface = SurfaceZ(point, raster, anchor, verticalExaggeration);

            if (surface is float sz)
            {
                float gap = point.Z - sz;
                if (previousGap is float pg && pg > 0f && gap <= 0f)
                {
                    // Bracketed a crossing between previousT (above) and t (below): refine and return.
                    return Bisect(ray.Origin, direction, raster, anchor, verticalExaggeration, previousT, t);
                }

                previousGap = gap;
            }
            else
            {
                // Over a gap: a crossing cannot be measured across undefined terrain.
                previousGap = null;
            }

            previousT = t;
            if (t >= maxDistanceMeters)
            {
                break;
            }
        }

        return null;
    }

    private static Vector3 Bisect(
        Vector3 origin,
        Vector3 direction,
        DemRaster raster,
        GeoPoint anchor,
        float verticalExaggeration,
        float tAbove,
        float tBelow)
    {
        for (int i = 0; i < BisectionIterations; i++)
        {
            float mid = 0.5f * (tAbove + tBelow);
            Vector3 point = origin + (direction * mid);
            float? surface = SurfaceZ(point, raster, anchor, verticalExaggeration);
            if (surface is not float sz)
            {
                // Should not happen between two in-bounds samples on a straight segment, but stay safe.
                break;
            }

            if (point.Z - sz > 0f)
            {
                tAbove = mid;
            }
            else
            {
                tBelow = mid;
            }
        }

        float finalT = 0.5f * (tAbove + tBelow);
        return origin + (direction * finalT);
    }

    /// <summary>
    /// Terrain surface Z (world units) at a world point's horizontal position, or null when the point
    /// falls outside the raster bounds or over no-data.
    /// </summary>
    private static float? SurfaceZ(Vector3 worldPoint, DemRaster raster, GeoPoint anchor, float verticalExaggeration)
    {
        GeoPoint geo = LocalTangentProjection.WorldToGeo(worldPoint, anchor);
        if (geo.Longitude < raster.West || geo.Longitude > raster.East ||
            geo.Latitude < raster.South || geo.Latitude > raster.North)
        {
            return null;
        }

        double elevation = raster.SampleBilinear(geo.Longitude, geo.Latitude);
        if (elevation == raster.NoDataValue)
        {
            return null;
        }

        return (float)(elevation * verticalExaggeration);
    }
}