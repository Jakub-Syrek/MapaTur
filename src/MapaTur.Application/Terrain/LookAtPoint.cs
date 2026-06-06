using System.Numerics;

using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Terrain;

/// <summary>
/// The look-at point: where the camera is actually aiming on the terrain, found by raycasting
/// from the eye through the screen centre and intersecting the DEM. This replaces
/// <see cref="Camera3D.Target"/> as the centre of the screen-space-error LOD — over mountains the
/// thing you are looking at can be kilometres away, so detail should follow the view ray, not the
/// camera position.
/// </summary>
public static class LookAtPoint
{
    // Cap the marched samples so a coarse DEM or a huge far plane can never blow up the step count;
    // the bracket only needs to be found, bisection then refines it to sub-metre accuracy.
    private const int MaxMarchSteps = 6000;

    /// <summary>
    /// Builds the eye ray through an arbitrary screen pixel by unprojecting the near and far plane
    /// points at that pixel. Pixel (0,0) is the top-left corner; Y grows downward.
    /// </summary>
    public static Ray ScreenPointRay(Camera3D camera, float pixelX, float pixelY, float screenWidth, float screenHeight)
    {
        ArgumentNullException.ThrowIfNull(camera);

        Matrix4x4 viewProjection = camera.BuildViewProjection(screenWidth / screenHeight);
        Matrix4x4.Invert(viewProjection, out Matrix4x4 inverse);

        float ndcX = (2f * pixelX / screenWidth) - 1f;
        float ndcY = 1f - (2f * pixelY / screenHeight); // screen Y is top-down; NDC Y is bottom-up.

        Vector3 near = UnprojectNdc(new Vector4(ndcX, ndcY, 0f, 1f), inverse);
        Vector3 far = UnprojectNdc(new Vector4(ndcX, ndcY, 1f, 1f), inverse);

        // Eye, near and far points are colinear along the pixel ray; take the direction from the
        // two plane points (numerically stable) and root the ray at the eye.
        Vector3 direction = Vector3.Normalize(far - near);
        return new Ray(camera.Position, direction);
    }

    /// <summary>Builds the eye ray through the centre of the screen.</summary>
    public static Ray ScreenCenterRay(Camera3D camera, float screenWidth, float screenHeight) =>
        ScreenPointRay(camera, screenWidth * 0.5f, screenHeight * 0.5f, screenWidth, screenHeight);

    /// <summary>
    /// Resolves the look-at point: the world-space terrain hit under the screen centre, or null when
    /// the centre ray meets no real terrain (looking at the sky, off the DEM, or over no-data).
    /// </summary>
    public static Vector3? Resolve(
        Camera3D camera,
        float screenWidth,
        float screenHeight,
        DemRaster raster,
        GeoPoint anchor,
        float verticalExaggeration)
    {
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(raster);
        if (screenWidth <= 0f || screenHeight <= 0f)
        {
            return null;
        }

        Ray ray = ScreenCenterRay(camera, screenWidth, screenHeight);
        (float maxDistance, float step) = MarchParameters(camera, raster);
        return TerrainRaycaster.Intersect(ray, raster, anchor, verticalExaggeration, maxDistance, step);
    }

    private static Vector3 UnprojectNdc(Vector4 ndc, Matrix4x4 inverseViewProjection)
    {
        Vector4 world = Vector4.Transform(ndc, inverseViewProjection);
        return new Vector3(world.X, world.Y, world.Z) / world.W;
    }

    /// <summary>
    /// Picks a march step (≈ half a DEM cell, for fidelity) and a max distance (the DEM diagonal plus
    /// a margin for camera height, capped to the far plane), then widens the step if needed so the
    /// sample count stays bounded.
    /// </summary>
    private static (float MaxDistance, float Step) MarchParameters(Camera3D camera, DemRaster raster)
    {
        double midLatitude = 0.5 * (raster.North + raster.South);
        double metersPerLon = LocalTangentProjection.MetersPerLatDegree * Math.Cos(midLatitude * Math.PI / 180.0);
        double widthMeters = (raster.East - raster.West) * metersPerLon;
        double heightMeters = (raster.North - raster.South) * LocalTangentProjection.MetersPerLatDegree;

        double cellX = widthMeters / (raster.Columns - 1);
        double cellY = heightMeters / (raster.Rows - 1);
        float step = MathF.Max(0.5f, 0.5f * (float)Math.Min(cellX, cellY));

        double diagonal = Math.Sqrt((widthMeters * widthMeters) + (heightMeters * heightMeters));
        float maxDistance = (float)Math.Min(camera.FarPlane, diagonal + (camera.Distance * 2.0));

        // Keep the marched sample count bounded regardless of DEM resolution / extent.
        if (maxDistance / step > MaxMarchSteps)
        {
            step = maxDistance / MaxMarchSteps;
        }

        return (maxDistance, step);
    }
}