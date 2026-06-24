using MapaTur.Domain.Geography;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Combines a primary gazetteer (OSM-sourced) with a fallback gazetteer (hand-maintained), keeping every
/// primary summit and adding only fallback summits that don't already appear in the primary set (matched
/// by proximity). Lets OSM drive the names and heights while a curated list backfills anything OSM omits,
/// without double-marking summits both sources know about.
/// </summary>
public static class SummitSources
{
    /// <summary>A fallback summit within this distance of a primary summit is treated as the same peak.</summary>
    public const double DefaultDedupeMeters = 250.0;

    public static IReadOnlyList<NamedSummit> Combine(
        IReadOnlyList<NamedSummit> primary,
        IReadOnlyList<NamedSummit> fallback,
        double dedupeMeters = DefaultDedupeMeters)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(fallback);

        var combined = new List<NamedSummit>(primary.Count + fallback.Count);
        combined.AddRange(primary);

        // The fallback list is the hand-curated set of iconic summits. When a curated summit matches a primary
        // (OSM) one, the OSM summit is kept (drives the name/height) but flagged Curated — that endorsement
        // lets the label de-clutter prefer it over a taller-but-lesser neighbour (keeps Kościelec, not Zadni
        // Kościelec). A curated summit with no OSM match is added on its own, also Curated.
        foreach (NamedSummit candidate in fallback)
        {
            int matchIndex = -1;
            for (int i = 0; i < primary.Count; i++)
            {
                if (candidate.Location.HaversineDistanceMetersTo(primary[i].Location) <= dedupeMeters)
                {
                    matchIndex = i;
                    break;
                }
            }

            if (matchIndex >= 0)
            {
                combined[matchIndex] = combined[matchIndex] with { Curated = true };
            }
            else
            {
                combined.Add(candidate with { Curated = true });
            }
        }

        return combined;
    }

    /// <summary>
    /// Collapses near-duplicate summits — a multi-node OSM massif published as several adjacent
    /// <c>natural=peak</c> nodes with the SAME name (e.g. two "Rysy" nodes) — keeping the highest of each
    /// cluster. Crucially it only merges nodes that share a name: DISTINCT named summits that happen to sit
    /// close together (Kościelec vs Zadni Kościelec ~180 m apart, the three Granaty along Orla Perć) are
    /// kept separately — collapsing them by distance alone silently dropped real peaks. On-screen label
    /// overlap between distinct neighbours is handled later by the renderer's footprint de-clutter.
    /// </summary>
    public static IReadOnlyList<NamedSummit> Deduplicate(
        IReadOnlyList<NamedSummit> summits,
        double dedupeMeters = DefaultDedupeMeters)
    {
        ArgumentNullException.ThrowIfNull(summits);

        // Highest first so each same-name cluster collapses onto its tallest node.
        List<NamedSummit> ordered = summits.OrderByDescending(s => s.ElevationMeters).ToList();
        var kept = new List<NamedSummit>(ordered.Count);
        foreach (NamedSummit candidate in ordered)
        {
            bool near = false;
            for (int i = 0; i < kept.Count; i++)
            {
                if (string.Equals(candidate.Name, kept[i].Name, StringComparison.Ordinal)
                    && candidate.Location.HaversineDistanceMetersTo(kept[i].Location) <= dedupeMeters)
                {
                    near = true;
                    break;
                }
            }

            if (!near)
            {
                kept.Add(candidate);
            }
        }

        return kept;
    }
}