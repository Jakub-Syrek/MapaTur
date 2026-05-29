using MapaTur.Domain.Geography;
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
    /// window or never made the elevation cap.
    /// <para>
    /// Each named summit is <b>snapped to the highest DEM cell within <paramref name="snapRadiusMeters"/></b>
    /// of its (approximate) gazetteer coordinate, then placed there with that cell's elevation. Sampling
    /// the gazetteer point directly is wrong for sharp peaks: a coordinate even ~200 m off the true summit
    /// lands on a slope and reads hundreds of metres too low (e.g. Świnica showing 1651 m instead of
    /// ~2300 m). Snapping to the local maximum puts the marker on the real summit with the right height.
    /// </para>
    /// Any detected peak within <paramref name="dedupeMeters"/> of a snapped named summit is dropped so the
    /// two don't double-mark. Pure and deterministic.
    /// </summary>
    /// <param name="detected">Detected (typically unnamed) summits from <see cref="PeakDetector.Detect"/>.</param>
    /// <param name="summits">Gazetteer of named summits.</param>
    /// <param name="raster">DEM used to snap each named summit to its real local maximum.</param>
    /// <param name="snapRadiusMeters">Search window around each gazetteer coordinate for the true summit cell.</param>
    /// <param name="dedupeMeters">A detected peak this close to a snapped named summit is treated as the same summit and dropped.</param>
    public static IReadOnlyList<TerrainPeak> MergeWithGazetteer(
        IReadOnlyList<TerrainPeak> detected,
        IReadOnlyList<NamedSummit> summits,
        DemRaster raster,
        double snapRadiusMeters = 500.0,
        double dedupeMeters = 400.0)
    {
        ArgumentNullException.ThrowIfNull(detected);
        ArgumentNullException.ThrowIfNull(summits);
        ArgumentNullException.ThrowIfNull(raster);

        var named = new List<TerrainPeak>(summits.Count);
        foreach (NamedSummit summit in summits)
        {
            double lon = summit.Location.Longitude;
            double lat = summit.Location.Latitude;
            if (lon < raster.West || lon > raster.East || lat < raster.South || lat > raster.North)
            {
                continue; // out of the loaded DEM — can't seat it on the terrain
            }

            // Seat the marker on the snapped DEM maximum, but label it with the gazetteer's published
            // height — the 60 m DEM under-reports sharp summits (e.g. Świnica), so the DEM value is right
            // for placement but wrong for the displayed elevation.
            (GeoPoint location, double elevation) = HighestCellNear(raster, summit.Location, snapRadiusMeters);
            named.Add(new TerrainPeak(location, elevation, summit.Name, summit.ElevationMeters));
        }

        var result = new List<TerrainPeak>(named.Count + detected.Count);
        result.AddRange(named);

        // Detected maxima fill the gaps, except those that coincide with a named summit already added.
        foreach (TerrainPeak peak in detected)
        {
            bool nearNamed = false;
            for (int n = 0; n < named.Count; n++)
            {
                if (peak.Location.HaversineDistanceMetersTo(named[n].Location) <= dedupeMeters)
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
    /// Finds the highest non-no-data DEM cell within <paramref name="radiusMeters"/> of <paramref name="point"/>
    /// and returns its geographic location and elevation. Falls back to the point itself (bilinear sample)
    /// when the whole window is no-data.
    /// </summary>
    private static (GeoPoint Location, double Elevation) HighestCellNear(DemRaster raster, GeoPoint point, double radiusMeters)
    {
        int cols = raster.Columns;
        int rows = raster.Rows;
        double colF = (point.Longitude - raster.West) / (raster.East - raster.West) * (cols - 1);
        double rowF = (raster.North - point.Latitude) / (raster.North - raster.South) * (rows - 1);
        int centerCol = (int)Math.Round(colF);
        int centerRow = (int)Math.Round(rowF);

        const double MetersPerLatDegree = 111_320.0;
        double centerLatDeg = (raster.North + raster.South) / 2.0;
        double metersPerLonDegree = MetersPerLatDegree * Math.Cos(centerLatDeg * Math.PI / 180.0);
        double cellLonMeters = (raster.East - raster.West) / (cols - 1) * metersPerLonDegree;
        double cellLatMeters = (raster.North - raster.South) / (rows - 1) * MetersPerLatDegree;
        double cellMeters = (cellLonMeters + cellLatMeters) / 2.0;
        int radius = cellMeters > 0.0 ? Math.Max(1, (int)Math.Round(radiusMeters / cellMeters)) : 1;

        int c0 = Math.Max(0, centerCol - radius);
        int c1 = Math.Min(cols - 1, centerCol + radius);
        int r0 = Math.Max(0, centerRow - radius);
        int r1 = Math.Min(rows - 1, centerRow + radius);

        float best = float.NegativeInfinity;
        int bestCol = centerCol;
        int bestRow = centerRow;
        for (int r = r0; r <= r1; r++)
        {
            for (int c = c0; c <= c1; c++)
            {
                float e = raster[c, r];
                if (e == raster.NoDataValue)
                {
                    continue;
                }

                if (e > best)
                {
                    best = e;
                    bestCol = c;
                    bestRow = r;
                }
            }
        }

        if (float.IsNegativeInfinity(best))
        {
            return (point, raster.SampleBilinear(point.Longitude, point.Latitude));
        }

        double snappedLon = raster.West + ((double)bestCol / (cols - 1) * (raster.East - raster.West));
        double snappedLat = raster.North - ((double)bestRow / (rows - 1) * (raster.North - raster.South));
        return (new GeoPoint(snappedLat, snappedLon), best);
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