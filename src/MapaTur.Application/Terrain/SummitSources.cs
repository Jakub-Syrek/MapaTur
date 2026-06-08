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

        foreach (NamedSummit candidate in fallback)
        {
            bool duplicate = false;
            for (int i = 0; i < primary.Count; i++)
            {
                if (candidate.Location.HaversineDistanceMetersTo(primary[i].Location) <= dedupeMeters)
                {
                    duplicate = true;
                    break;
                }
            }

            if (!duplicate)
            {
                combined.Add(candidate);
            }
        }

        return combined;
    }

    /// <summary>
    /// Collapses near-duplicate summits within <paramref name="dedupeMeters"/> of each other, keeping the
    /// highest of each cluster. OSM publishes multi-summit massifs (Rysy, Wysoka) as several adjacent
    /// <c>natural=peak</c> nodes; without this the overlay stacks two or three labels on one apex.
    /// </summary>
    public static IReadOnlyList<NamedSummit> Deduplicate(
        IReadOnlyList<NamedSummit> summits,
        double dedupeMeters = DefaultDedupeMeters)
    {
        ArgumentNullException.ThrowIfNull(summits);

        // Highest first so each cluster collapses onto its tallest summit.
        List<NamedSummit> ordered = summits.OrderByDescending(s => s.ElevationMeters).ToList();
        var kept = new List<NamedSummit>(ordered.Count);
        foreach (NamedSummit candidate in ordered)
        {
            bool near = false;
            for (int i = 0; i < kept.Count; i++)
            {
                if (candidate.Location.HaversineDistanceMetersTo(kept[i].Location) <= dedupeMeters)
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