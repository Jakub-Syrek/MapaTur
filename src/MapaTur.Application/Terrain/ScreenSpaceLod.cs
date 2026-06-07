using System.Numerics;

using MapaTur.Domain.Geography;

namespace MapaTur.Application.Terrain;

/// <summary>One ring cell and the detail zoom assigned to it by the screen-space-error LOD.</summary>
public readonly record struct LookAtLodTile(DemTileKey Cell, int Zoom);

/// <summary>
/// Krok 3 (screen-space-error LOD, Model 2): assigns a detail zoom per tile around the look-at point
/// using <see cref="ScreenSpaceError"/> instead of the fixed distance bands of <see cref="TerrainLodBands"/>.
/// The detail ring is centred on the look-at (Krok 1) and each cell's zoom is chosen so its on-screen
/// error stays within a pixel budget, giving a concentric falloff with the finest detail where the camera
/// is aimed. The per-zoom geometric error is approximated by the tile's ground resolution; a later Model 1
/// step replaces that proxy with measured per-tile terrain roughness.
/// </summary>
public static class ScreenSpaceLod
{
    // Web-Mercator ground resolution (metres/pixel) at zoom 0 on the equator. Matches DemTilePlanner.
    private const double EquatorMetersPerPixelZoom0 = 156543.03392;

    /// <summary>Ground resolution (metres per pixel) of a tile at <paramref name="zoom"/> and latitude.</summary>
    public static double MetersPerPixel(int zoom, double latitudeDegrees)
    {
        double cosLat = Math.Cos(latitudeDegrees * Math.PI / 180.0);
        return EquatorMetersPerPixelZoom0 * cosLat / Math.Pow(2.0, zoom);
    }

    /// <summary>
    /// Turns a tile's roughness (e.g. <see cref="DemRasterRoughness.Roughness"/>) into the LOD weight used as
    /// <c>tilePriority = screenSpaceError × roughnessFactor</c> (Model 1): <c>1 + clamp(roughness / reference,
    /// 0, maxBoost)</c>. The factor is always ≥ 1, so screen-space error stays the decision base and roughness
    /// only BOOSTS geometrically interesting tiles — a flat valley weighs 1 (no penalty), a ridge weighs more
    /// (finer detail from farther), capped at 1 + maxBoost.
    /// </summary>
    /// <param name="roughnessMeters">The tile's measured local roughness, in metres.</param>
    /// <param name="referenceRoughnessMeters">Roughness that adds a full +1 boost (tuning anchor); ≤ 0 returns 1.</param>
    /// <param name="maxBoost">Upper clamp on the additive boost, so one extreme tile can't demand unbounded detail.</param>
    public static double RoughnessFactor(double roughnessMeters, double referenceRoughnessMeters, double maxBoost)
    {
        if (referenceRoughnessMeters <= 0.0)
        {
            return 1.0;
        }

        double boost = Math.Clamp(roughnessMeters / referenceRoughnessMeters, 0.0, maxBoost);
        return 1.0 + boost;
    }

    /// <summary>
    /// Chooses a detail zoom for a tile at <paramref name="cameraDistanceMeters"/> from the camera, using
    /// each candidate zoom's ground resolution as its geometric error. Returns the coarsest (lowest) zoom
    /// whose on-screen error stays within <paramref name="maxErrorPixels"/>, falling back to the finest
    /// candidate when even it overshoots.
    /// </summary>
    /// <param name="zoomCandidatesFinestFirst">Candidate zooms, finest first (largest zoom = smallest error).</param>
    /// <param name="cameraDistanceMeters">Distance from the camera to the tile, in metres.</param>
    /// <param name="latitudeDegrees">Tile latitude, used to scale ground resolution by cos(lat).</param>
    /// <param name="fovY">Vertical field of view, in radians.</param>
    /// <param name="viewportHeight">Viewport height, in pixels.</param>
    /// <param name="maxErrorPixels">Pixel error budget; the coarsest zoom within it is chosen.</param>
    /// <param name="roughnessFactor">Model 1 weight (default 1). Scales the screen-space error so a rough
    /// tile (ridge/wall) earns a finer zoom from the same distance and a smooth valley grades down — i.e.
    /// <c>tilePriority = screenSpaceError × roughnessFactor</c>. See <see cref="RoughnessFactor"/>.</param>
    public static int ZoomForCameraDistance(
        IReadOnlyList<int> zoomCandidatesFinestFirst,
        double cameraDistanceMeters,
        double latitudeDegrees,
        double fovY,
        double viewportHeight,
        double maxErrorPixels,
        double roughnessFactor = 1.0)
    {
        ArgumentNullException.ThrowIfNull(zoomCandidatesFinestFirst);
        if (zoomCandidatesFinestFirst.Count == 0)
        {
            throw new ArgumentException("At least one candidate zoom is required.", nameof(zoomCandidatesFinestFirst));
        }

        var geometricErrors = new double[zoomCandidatesFinestFirst.Count];
        for (int i = 0; i < zoomCandidatesFinestFirst.Count; i++)
        {
            geometricErrors[i] = MetersPerPixel(zoomCandidatesFinestFirst[i], latitudeDegrees) * roughnessFactor;
        }

        int level = ScreenSpaceError.SelectLod(geometricErrors, cameraDistanceMeters, fovY, viewportHeight, maxErrorPixels);
        return zoomCandidatesFinestFirst[level];
    }

    /// <summary>
    /// Assigns a detail zoom to every cell of a ring centred on <paramref name="lookAt"/>, by projecting
    /// each cell centre into the world frame and choosing the zoom from its camera distance. The finest
    /// detail lands on the look-at cell and coarsens toward the ring edges.
    /// </summary>
    /// <param name="lookAt">Ground point the camera is aimed at (Krok 1 look-at).</param>
    /// <param name="lookAtElevationMeters">Elevation used for every cell's Z when measuring camera distance.</param>
    /// <param name="cameraPosition">Camera position in the world frame (X east, Y north, Z up).</param>
    /// <param name="projectionAnchor">Shared projection origin tying the world frame to geographic coordinates.</param>
    /// <param name="verticalExaggeration">Z scale matching the rendered terrain.</param>
    /// <param name="zoomCandidatesFinestFirst">Candidate detail zooms, finest first.</param>
    /// <param name="gridZoom">Zoom of the ring cells enumerated around the look-at.</param>
    /// <param name="radiusTiles">Ring half-width in cells (0 = look-at cell only, 1 = 3×3, 2 = 5×5).</param>
    /// <param name="fovY">Vertical field of view, in radians.</param>
    /// <param name="viewportHeight">Viewport height, in pixels.</param>
    /// <param name="maxErrorPixels">Pixel error budget; the coarsest zoom within it is chosen per cell.</param>
    public static IReadOnlyList<LookAtLodTile> AssignAroundLookAt(
        GeoPoint lookAt,
        float lookAtElevationMeters,
        Vector3 cameraPosition,
        GeoPoint projectionAnchor,
        float verticalExaggeration,
        IReadOnlyList<int> zoomCandidatesFinestFirst,
        int gridZoom,
        int radiusTiles,
        double fovY,
        double viewportHeight,
        double maxErrorPixels)
    {
        IReadOnlyList<DemTileKey> ring = DetailTileRing.Around(lookAt, gridZoom, radiusTiles);
        var assignments = new List<LookAtLodTile>(ring.Count);

        foreach (DemTileKey cell in ring)
        {
            (double west, double south, double east, double north) = SlippyTileMath.TileBounds(cell.X, cell.Y, cell.Zoom);
            var centre = new GeoPoint((south + north) / 2.0, (west + east) / 2.0);
            Vector3 world = LocalTangentProjection.GeoToWorld(centre, lookAtElevationMeters, projectionAnchor, verticalExaggeration);
            double distance = Vector3.Distance(cameraPosition, world);
            int zoom = ZoomForCameraDistance(zoomCandidatesFinestFirst, distance, centre.Latitude, fovY, viewportHeight, maxErrorPixels);
            assignments.Add(new LookAtLodTile(cell, zoom));
        }

        return assignments;
    }
}