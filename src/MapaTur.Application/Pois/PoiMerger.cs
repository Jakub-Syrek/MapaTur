using MapaTur.Domain.Pois;

namespace MapaTur.Application.Pois;

/// <summary>
/// Merges the POI set actually shown + searched: DOWNLOADED (OSM) entries win, BUNDLED (curated gazetteer)
/// entries fill what's missing. A bundled entry is skipped when a downloaded one already covers the same
/// real-world feature — matched EITHER by name (case-insensitive) OR by proximity within
/// <see cref="ProximityDedupeMeters"/> for the same <see cref="PoiKind"/>. The proximity rule exists because
/// name matching alone stacked two labels on one building ("Schronisko nad Morskim Okiem" curated vs
/// "Schronisko PTTK Morskie Oko" downloaded — identical coordinates, different names). Downloaded names in
/// the suppressed set are dropped entirely (a curated POI is hand-pinned on that exact node — the Honoratka
/// case). Pure and deterministic.
/// </summary>
public static class PoiMerger
{
    /// <summary>Downloaded POI of the same kind within this range = the same real-world feature; the bundled
    /// twin is skipped. Two REAL neighbouring huts (Morskie Oko new vs old building) sit ~90 m apart in OSM
    /// as separate DOWNLOADED nodes — the radius only guards bundled-vs-downloaded, never downloaded pairs.</summary>
    public const double ProximityDedupeMeters = 80.0;

    /// <summary>Merges downloaded and bundled POIs (see class doc).</summary>
    /// <param name="downloaded">Downloaded/cached OSM POIs; null = none.</param>
    /// <param name="bundled">Curated gazetteer entries (huts + passes).</param>
    /// <param name="suppressedDownloadedNames">Downloaded names to drop entirely (case-insensitive), or null.</param>
    public static IReadOnlyList<MountainPoi> Merge(
        IReadOnlyList<MountainPoi>? downloaded,
        IReadOnlyList<MountainPoi> bundled,
        IReadOnlySet<string>? suppressedDownloadedNames)
    {
        ArgumentNullException.ThrowIfNull(bundled);

        var result = new List<MountainPoi>((downloaded?.Count ?? 0) + bundled.Count);
        if (downloaded is not null)
        {
            foreach (MountainPoi poi in downloaded)
            {
                if (suppressedDownloadedNames is null || !suppressedDownloadedNames.Contains(poi.Name))
                {
                    result.Add(poi);
                }
            }
        }

        var downloadedNames = new HashSet<string>(result.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        int downloadedCount = result.Count;
        foreach (MountainPoi candidate in bundled)
        {
            if (downloadedNames.Contains(candidate.Name))
            {
                continue; // same name already arrived from OSM
            }

            bool coveredByProximity = false;
            for (int i = 0; i < downloadedCount; i++)
            {
                MountainPoi existing = result[i];
                if (existing.Kind == candidate.Kind
                    && existing.Position.HaversineDistanceMetersTo(candidate.Position) <= ProximityDedupeMeters)
                {
                    coveredByProximity = true; // same feature under a different name — downloaded wins
                    break;
                }
            }

            if (!coveredByProximity)
            {
                result.Add(candidate);
                downloadedNames.Add(candidate.Name);
            }
        }

        return result;
    }
}