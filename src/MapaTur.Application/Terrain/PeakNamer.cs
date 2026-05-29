using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Assigns names to DEM-detected <see cref="TerrainPeak"/>s by matching each against a gazetteer of
/// <see cref="NamedSummit"/>s. Pure, offline, deterministic.
/// </summary>
public static class PeakNamer
{
    /// <summary>
    /// Builds the 3D peak overlay so that EVERY in-bounds gazetteer summit is shown with its name,
    /// then fills the rest of the map with detected (unnamed) maxima. Relying on detection-then-match
    /// alone left most known summits unnamed — their nearest detected maximum fell outside the match
    /// window, or never made the elevation cap. Here each named summit is placed directly at its own
    /// location and seated on the terrain via a DEM elevation sample, and any detected peak within
    /// <paramref name="dedupeMeters"/> of a named summit is dropped so the two don't double-mark.
    /// Pure and deterministic.
    /// </summary>
    /// <param name="detected">Detected (typically unnamed) summits from <see cref="PeakDetector.Detect"/>.</param>
    /// <param name="summits">Gazetteer of named summits.</param>
    /// <param name="raster">DEM used to seat each named summit on the rendered terrain.</param>
    /// <param name="dedupeMeters">A detected peak this close to a named summit is treated as the same summit and dropped.</param>
    public static IReadOnlyList<TerrainPeak> MergeWithGazetteer(
        IReadOnlyList<TerrainPeak> detected,
        IReadOnlyList<NamedSummit> summits,
        DemRaster raster,
        double dedupeMeters = 600.0)
    {
        ArgumentNullException.ThrowIfNull(detected);
        ArgumentNullException.ThrowIfNull(summits);
        ArgumentNullException.ThrowIfNull(raster);

        var result = new List<TerrainPeak>(summits.Count + detected.Count);

        // Every named summit inside the loaded DEM appears, named, at its sampled terrain elevation.
        foreach (NamedSummit summit in summits)
        {
            double lon = summit.Location.Longitude;
            double lat = summit.Location.Latitude;
            if (lon < raster.West || lon > raster.East || lat < raster.South || lat > raster.North)
            {
                continue; // out of the loaded DEM — can't seat it on the terrain
            }

            double elevation = raster.SampleBilinear(lon, lat);
            result.Add(new TerrainPeak(summit.Location, elevation, summit.Name));
        }

        // Detected maxima fill the gaps, except those that coincide with a named summit already added.
        foreach (TerrainPeak peak in detected)
        {
            bool nearNamed = false;
            for (int s = 0; s < summits.Count; s++)
            {
                if (peak.Location.HaversineDistanceMetersTo(summits[s].Location) <= dedupeMeters)
                {
                    nearNamed = true;
                    break;
                }
            }

            if (!nearNamed)
            {
                result.Add(peak);
            }
        }

        result.Sort((a, b) => b.ElevationMeters.CompareTo(a.ElevationMeters));
        return result;
    }

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