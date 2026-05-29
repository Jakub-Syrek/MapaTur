namespace MapaTur.Application.Terrain;

/// <summary>
/// Assigns names to DEM-detected <see cref="TerrainPeak"/>s by matching each against a gazetteer of
/// <see cref="NamedSummit"/>s. Pure, offline, deterministic.
/// </summary>
public static class PeakNamer
{
    /// <summary>
    /// Returns a copy of <paramref name="peaks"/> where each peak is named after the closest
    /// <see cref="NamedSummit"/> within <paramref name="maxMatchMeters"/>; peaks with no summit in
    /// range keep their existing (typically null) name. Location and elevation are preserved.
    /// </summary>
    /// <param name="peaks">Detected summits to name.</param>
    /// <param name="summits">Gazetteer of named summits to match against.</param>
    /// <param name="maxMatchMeters">Maximum great-circle distance for a match, in metres.</param>
    public static IReadOnlyList<TerrainPeak> AssignNames(
        IReadOnlyList<TerrainPeak> peaks,
        IReadOnlyList<NamedSummit> summits,
        double maxMatchMeters = 800.0)
    {
        ArgumentNullException.ThrowIfNull(peaks);
        ArgumentNullException.ThrowIfNull(summits);

        if (peaks.Count == 0)
        {
            return Array.Empty<TerrainPeak>();
        }

        var named = new TerrainPeak[peaks.Count];
        for (int i = 0; i < peaks.Count; i++)
        {
            TerrainPeak peak = peaks[i];
            string? bestName = null;
            double bestDistance = maxMatchMeters;
            for (int s = 0; s < summits.Count; s++)
            {
                NamedSummit summit = summits[s];
                double distance = peak.Location.HaversineDistanceMetersTo(summit.Location);
                if (distance <= bestDistance)
                {
                    bestDistance = distance;
                    bestName = summit.Name;
                }
            }

            named[i] = bestName is null ? peak : peak with { Name = bestName };
        }

        return named;
    }
}