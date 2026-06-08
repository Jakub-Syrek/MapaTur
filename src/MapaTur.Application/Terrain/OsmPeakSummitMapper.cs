namespace MapaTur.Application.Terrain;

/// <summary>
/// Projects OSM <c>natural=peak</c> nodes onto the <see cref="NamedSummit"/> gazetteer shape consumed by
/// <see cref="PeakNamer.MergeWithGazetteer"/>. Keeps only peaks that are both named and at or above an
/// elevation threshold — <see cref="NamedSummit"/> requires a name and a height, and the threshold drops
/// the many minor named knolls OSM carries below the main ridge.
/// </summary>
public static class OsmPeakSummitMapper
{
    /// <summary>Default cut-off: named summits at or above 1500 m (keeps Giewont/Sarnia ridge, drops foothills).</summary>
    public const double DefaultMinElevationMeters = 1500.0;

    public static IReadOnlyList<NamedSummit> ToSummits(
        IReadOnlyList<OsmPeak> peaks,
        double minElevationMeters = DefaultMinElevationMeters)
    {
        ArgumentNullException.ThrowIfNull(peaks);

        var summits = new List<NamedSummit>(peaks.Count);
        foreach (OsmPeak peak in peaks)
        {
            if (string.IsNullOrWhiteSpace(peak.Name))
            {
                continue;
            }

            if (peak.ElevationMeters is not double elevation || elevation < minElevationMeters)
            {
                continue;
            }

            summits.Add(new NamedSummit(peak.Name, peak.Position, elevation));
        }

        return summits;
    }
}