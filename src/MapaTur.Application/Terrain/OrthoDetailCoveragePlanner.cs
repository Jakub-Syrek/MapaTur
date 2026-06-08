using MapaTur.Domain.Geography;

namespace MapaTur.Application.Terrain;

/// <summary>Resolved near-field ortho coverage: the ESRI zoom to fetch and the cell grid to composite.</summary>
public sealed record OrthoDetailCoverage(int Zoom, int GridCols, int GridRows, int CellPixels)
{
    /// <summary>Approximate resident VRAM for the whole grid: RGBA8 bytes plus 1/3 for the mip chain.</summary>
    public long ResidentBytes => (long)GridCols * GridRows * CellPixels * CellPixels * 4L * 4L / 3L;
}

/// <summary>Tuning for <see cref="OrthoDetailCoveragePlanner"/>.</summary>
/// <param name="ZoomCandidatesFinestFirst">Candidate ESRI zooms, finest first (e.g. 18,17,…,13).</param>
/// <param name="MaxErrorPixels">On-screen error budget that caps the chosen zoom for the camera distance.</param>
/// <param name="MaxCellPixels">Largest single-cell texture side (keeps each composite + GL upload bounded).</param>
/// <param name="MaxResidentBytes">VRAM budget for the whole near-field grid; the planner drops zoom to fit.</param>
public sealed record OrthoDetailOptions(
    IReadOnlyList<int> ZoomCandidatesFinestFirst,
    double MaxErrorPixels = 1.5,
    int MaxCellPixels = 2048,
    long MaxResidentBytes = 512L * 1024 * 1024);

/// <summary>
/// Plans the high-zoom near-field orthophoto coverage that replaces the coarse whole-scene drape (one 4096²
/// texture ≈ 16 m/px). Picks the ESRI zoom whose on-screen error stays within budget — capped by the
/// camera→look-at distance so it never over-fetches — then sizes a cell grid that covers the look-at window
/// at that zoom, dropping to a coarser zoom if the fine grid would blow the VRAM budget. Pure and deterministic.
/// </summary>
public static class OrthoDetailCoveragePlanner
{
    private const double MetersPerLatDegree = 111_320.0;

    public static OrthoDetailCoverage Plan(
        MapBounds window,
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

        double latitude = window.Center.Latitude;
        double widthMeters = (window.NorthEast.Longitude - window.SouthWest.Longitude)
            * MetersPerLatDegree * Math.Cos(latitude * Math.PI / 180.0);
        double heightMeters = (window.NorthEast.Latitude - window.SouthWest.Latitude) * MetersPerLatDegree;

        // Ceiling zoom: no point fetching finer than the on-screen error justifies for this camera distance.
        int idealZoom = ScreenSpaceLod.ZoomForCameraDistance(
            candidates, cameraToLookAtMeters, latitude, fovY, viewportHeight, options.MaxErrorPixels);

        // Candidates are finest-first; start at the ideal zoom and walk toward coarser ones, taking the first
        // whose grid fits the VRAM budget.
        int startIndex = 0;
        for (int i = 0; i < candidates.Count; i++)
        {
            if (candidates[i] == idealZoom)
            {
                startIndex = i;
                break;
            }
        }

        OrthoDetailCoverage coverage = ForZoom(candidates[startIndex], widthMeters, heightMeters, latitude, options.MaxCellPixels);
        for (int i = startIndex; i < candidates.Count; i++)
        {
            coverage = ForZoom(candidates[i], widthMeters, heightMeters, latitude, options.MaxCellPixels);
            if (coverage.ResidentBytes <= options.MaxResidentBytes)
            {
                return coverage;
            }
        }

        // Even the coarsest candidate overshoots the budget — return it as best effort.
        return coverage;
    }

    private static OrthoDetailCoverage ForZoom(int zoom, double widthMeters, double heightMeters, double latitude, int maxCellPixels)
    {
        double metersPerPixel = ScreenSpaceLod.MetersPerPixel(zoom, latitude);
        int neededPxX = Math.Max(1, (int)Math.Ceiling(widthMeters / metersPerPixel));
        int neededPxY = Math.Max(1, (int)Math.Ceiling(heightMeters / metersPerPixel));
        int cellPixels = Math.Min(maxCellPixels, Math.Max(256, Math.Max(neededPxX, neededPxY)));
        int gridCols = (int)Math.Ceiling((double)neededPxX / cellPixels);
        int gridRows = (int)Math.Ceiling((double)neededPxY / cellPixels);
        return new OrthoDetailCoverage(zoom, gridCols, gridRows, cellPixels);
    }
}