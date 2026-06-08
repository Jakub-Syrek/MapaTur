using MapaTur.Domain.Geography;

namespace MapaTur.Application.Terrain;

/// <summary>Whether high-zoom near-field ortho is worth streaming for this view.</summary>
public enum OrthoDetailDecision
{
    /// <summary>Fetch + composite the near-field ortho with the returned grid — it beats the base drape.</summary>
    Stream,

    /// <summary>The best feasible zoom is no finer than the base drape — keep the base, stream nothing.</summary>
    SkipNoGain,
}

/// <summary>
/// The complete near-field ortho decision for one view: whether to stream, the geographic window, the ESRI
/// zoom, the cell grid + cell size, the chosen ground resolution, and the estimated resident VRAM.
/// </summary>
public sealed record OrthoDetailCoverage(
    OrthoDetailDecision Decision,
    MapBounds Window,
    int Zoom,
    int GridCols,
    int GridRows,
    int CellPixels,
    double MetersPerPixel,
    long EstimatedVramBytes);

/// <summary>Tuning for <see cref="OrthoDetailCoveragePlanner"/>.</summary>
/// <param name="ZoomCandidatesFinestFirst">Candidate ESRI zooms, finest first (e.g. 18,17,…,13).</param>
/// <param name="MaxErrorPixels">On-screen error budget that caps the chosen zoom for the camera distance.</param>
/// <param name="MaxCellPixels">Largest single-cell texture side (keeps each composite + GL upload bounded).</param>
/// <param name="MaxResidentBytes">VRAM budget for the whole near-field grid; the planner drops zoom to fit.</param>
/// <param name="BaseMetersPerPixel">Resolution of the base whole-scene drape; the near-field must beat it to
/// be worth streaming (otherwise <see cref="OrthoDetailDecision.SkipNoGain"/>). Default ≈ the 4096² drape.</param>
public sealed record OrthoDetailOptions(
    IReadOnlyList<int> ZoomCandidatesFinestFirst,
    double MaxErrorPixels = 1.5,
    int MaxCellPixels = 2048,
    long MaxResidentBytes = 512L * 1024 * 1024,
    double BaseMetersPerPixel = 16.0);

/// <summary>
/// Plans the high-zoom near-field orthophoto coverage that replaces the coarse whole-scene drape (one 4096²
/// texture ≈ 16 m/px). Picks the ESRI zoom whose on-screen error stays within budget — capped by the
/// camera→look-at distance so it never over-fetches — then sizes a cell grid that covers the look-at window
/// at that zoom, dropping to a coarser zoom if the fine grid would blow the VRAM budget. Reports a single
/// decision object (no renderer/network involved). Pure and deterministic.
/// </summary>
public static class OrthoDetailCoveragePlanner
{
    private const double MetersPerLatDegree = 111_320.0;

    public static OrthoDetailCoverage Plan(
        GeoPoint focus,
        double radiusMeters,
        double cameraToLookAtMeters,
        double fovY,
        double viewportHeight,
        OrthoDetailOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.ZoomCandidatesFinestFirst);
        IReadOnlyList<int> candidates = options.ZoomCandidatesFinestFirst;
        if (candidates.Count == 0)
        {
            throw new ArgumentException("At least one candidate zoom is required.", nameof(options));
        }

        MapBounds window = LodTerrainWindow.Around(focus, radiusMeters);
        double latitude = window.Center.Latitude;
        double widthMeters = (window.NorthEast.Longitude - window.SouthWest.Longitude)
            * MetersPerLatDegree * Math.Cos(latitude * Math.PI / 180.0);
        double heightMeters = (window.NorthEast.Latitude - window.SouthWest.Latitude) * MetersPerLatDegree;

        // Ceiling zoom: no point fetching finer than the on-screen error justifies for this camera distance.
        int idealZoom = ScreenSpaceLod.ZoomForCameraDistance(
            candidates, cameraToLookAtMeters, latitude, fovY, viewportHeight, options.MaxErrorPixels);

        // Candidates are finest-first; start at the ideal zoom and walk toward coarser ones, taking the first
        // grid that fits the VRAM budget (else the coarsest, as best effort).
        int startIndex = 0;
        for (int i = 0; i < candidates.Count; i++)
        {
            if (candidates[i] == idealZoom)
            {
                startIndex = i;
                break;
            }
        }

        int zoom = candidates[startIndex];
        (int gridCols, int gridRows, int cellPixels, long vram) = GridForZoom(zoom, widthMeters, heightMeters, latitude, options.MaxCellPixels);
        for (int i = startIndex; i < candidates.Count; i++)
        {
            (gridCols, gridRows, cellPixels, vram) = GridForZoom(candidates[i], widthMeters, heightMeters, latitude, options.MaxCellPixels);
            zoom = candidates[i];
            if (vram <= options.MaxResidentBytes)
            {
                break;
            }
        }

        double metersPerPixel = ScreenSpaceLod.MetersPerPixel(zoom, latitude);
        OrthoDetailDecision decision = metersPerPixel < options.BaseMetersPerPixel
            ? OrthoDetailDecision.Stream
            : OrthoDetailDecision.SkipNoGain;

        return new OrthoDetailCoverage(decision, window, zoom, gridCols, gridRows, cellPixels, metersPerPixel, vram);
    }

    private static (int GridCols, int GridRows, int CellPixels, long Vram) GridForZoom(
        int zoom, double widthMeters, double heightMeters, double latitude, int maxCellPixels)
    {
        double metersPerPixel = ScreenSpaceLod.MetersPerPixel(zoom, latitude);
        int neededPxX = Math.Max(1, (int)Math.Ceiling(widthMeters / metersPerPixel));
        int neededPxY = Math.Max(1, (int)Math.Ceiling(heightMeters / metersPerPixel));
        int cellPixels = Math.Min(maxCellPixels, Math.Max(256, Math.Max(neededPxX, neededPxY)));
        int gridCols = (int)Math.Ceiling((double)neededPxX / cellPixels);
        int gridRows = (int)Math.Ceiling((double)neededPxY / cellPixels);
        // RGBA8 bytes for the whole grid plus 1/3 for the mip chain.
        long vram = (long)gridCols * gridRows * cellPixels * cellPixels * 4L * 4L / 3L;
        return (gridCols, gridRows, cellPixels, vram);
    }
}