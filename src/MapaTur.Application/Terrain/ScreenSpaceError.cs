namespace MapaTur.Application.Terrain;

/// <summary>
/// Screen-space-error metric for the look-at LOD: how many screen pixels a tile's geometric error
/// spans at a given distance, and the LOD level that keeps that error within a pixel budget.
///
/// The projection is the standard perspective relation — a world-space error of
/// <c>geometricError</c> metres at <c>distance</c> metres subtends
/// <c>geometricError · viewportHeight / (2 · distance · tan(fov/2))</c> pixels. Detail is then
/// assigned by how much a tile occupies the screen, not by raw distance: a large massif kilometres
/// away can out-rank a small patch close to the camera.
/// </summary>
public static class ScreenSpaceError
{
    /// <summary>
    /// Projects a world-space geometric error to its size in screen pixels. Returns
    /// <see cref="double.PositiveInfinity"/> for a non-positive distance (the error fills the screen).
    /// </summary>
    /// <param name="geometricErrorMeters">Geometric error of the representation, in metres.</param>
    /// <param name="distanceMeters">Distance from the camera to the tile, in metres.</param>
    /// <param name="fieldOfViewYRadians">Vertical field of view, in radians.</param>
    /// <param name="viewportHeightPixels">Viewport height, in pixels.</param>
    public static double InPixels(
        double geometricErrorMeters,
        double distanceMeters,
        double fieldOfViewYRadians,
        double viewportHeightPixels)
    {
        if (distanceMeters <= 0.0)
        {
            return double.PositiveInfinity;
        }

        double projectionFactor = viewportHeightPixels / (2.0 * Math.Tan(fieldOfViewYRadians / 2.0));
        return geometricErrorMeters * projectionFactor / distanceMeters;
    }

    /// <summary>
    /// Chooses a LOD level for a tile at <paramref name="distanceMeters"/>, given the geometric error of
    /// each level ordered finest first (index 0 = smallest error). Returns the coarsest level whose
    /// projected error stays within <paramref name="maxErrorPixels"/>; if even the finest exceeds the
    /// budget, returns the finest (index 0) — the best the data can do.
    /// </summary>
    /// <param name="geometricErrorByLevelMeters">Per-level geometric error, finest (index 0) to coarsest.</param>
    /// <param name="distanceMeters">Distance from the camera to the tile, in metres.</param>
    /// <param name="fieldOfViewYRadians">Vertical field of view, in radians.</param>
    /// <param name="viewportHeightPixels">Viewport height, in pixels.</param>
    /// <param name="maxErrorPixels">Pixel error budget; the coarsest level within it is chosen.</param>
    public static int SelectLod(
        IReadOnlyList<double> geometricErrorByLevelMeters,
        double distanceMeters,
        double fieldOfViewYRadians,
        double viewportHeightPixels,
        double maxErrorPixels)
    {
        ArgumentNullException.ThrowIfNull(geometricErrorByLevelMeters);
        if (geometricErrorByLevelMeters.Count == 0)
        {
            throw new ArgumentException("At least one LOD level is required.", nameof(geometricErrorByLevelMeters));
        }

        // Error grows with the level index, so projected error grows too: the levels that fit the budget
        // form a prefix [0..k]. Walk from finest upward, keeping the coarsest that still fits.
        int chosen = 0;
        for (int level = 0; level < geometricErrorByLevelMeters.Count; level++)
        {
            double pixels = InPixels(geometricErrorByLevelMeters[level], distanceMeters, fieldOfViewYRadians, viewportHeightPixels);
            if (pixels <= maxErrorPixels)
            {
                chosen = level;
            }
            else
            {
                break;
            }
        }

        return chosen;
    }
}