using System.Globalization;
using System.Text;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Pois;
using MapaTur.Domain.Routing;

namespace MapaTur.Application.Routing;

/// <summary>
/// The searchable place picker behind the tourist-map route builder: peaks, lakes and POIs are unioned
/// into one list of <see cref="RouteWaypoint"/> and searched by name independent of case and Polish/
/// Slovak diacritics (so "lomnica" finds "Łomnica" and "strbske" finds "Štrbské pleso"). Results rank
/// prefix matches above mid-word ones, then by elevation so the prominent places lead. Pure — the VM
/// rebuilds it whenever the peak/lake/POI sets change.
/// </summary>
public sealed class PlaceGazetteer
{
    private readonly List<(RouteWaypoint Waypoint, string Folded)> entries;

    /// <summary>Builds the gazetteer from the current named-place sets (unnamed entries are dropped).</summary>
    public PlaceGazetteer(
        IEnumerable<TerrainPeak>? peaks = null,
        IEnumerable<MountainLake>? lakes = null,
        IEnumerable<MountainPoi>? pois = null)
    {
        entries = new List<(RouteWaypoint, string)>();

        if (peaks is not null)
        {
            foreach (TerrainPeak p in peaks)
            {
                if (string.IsNullOrWhiteSpace(p.Name)) { continue; }
                Add(new RouteWaypoint(p.Name!, p.Location, WaypointKind.Peak, p.LabelElevationMeters ?? p.ElevationMeters));
            }
        }

        if (lakes is not null)
        {
            foreach (MountainLake l in lakes)
            {
                if (string.IsNullOrWhiteSpace(l.Name)) { continue; }
                Add(new RouteWaypoint(l.Name, l.Centroid, WaypointKind.Lake, l.ElevationMeters));
            }
        }

        if (pois is not null)
        {
            foreach (MountainPoi poi in pois)
            {
                if (string.IsNullOrWhiteSpace(poi.Name)) { continue; }
                Add(new RouteWaypoint(poi.Name, poi.Position, WaypointKind.Hut, poi.ElevationMeters));
            }
        }
    }

    private void Add(RouteWaypoint w) => entries.Add((w, Fold(w.Name)));

    /// <summary>Every named place, peaks/lakes/huts unioned (unranked).</summary>
    public IReadOnlyList<RouteWaypoint> All => entries.Select(e => e.Waypoint).ToList();

    /// <summary>
    /// Returns up to <paramref name="max"/> places matching <paramref name="query"/> by folded-name
    /// substring, prefix matches first then by descending elevation. An empty/whitespace query returns
    /// the most prominent places (highest first), which makes the picker useful before the user types.
    /// </summary>
    public IReadOnlyList<RouteWaypoint> Search(string query, int max)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(max);
        string folded = Fold(query ?? string.Empty);

        IEnumerable<(RouteWaypoint Waypoint, int Rank)> matches;
        if (folded.Length == 0)
        {
            matches = entries.Select(e => (e.Waypoint, Rank: 1));
        }
        else
        {
            matches = entries
                .Where(e => e.Folded.Contains(folded, StringComparison.Ordinal))
                .Select(e => (e.Waypoint, Rank: e.Folded.StartsWith(folded, StringComparison.Ordinal) ? 0 : 1));
        }

        return matches
            .OrderBy(m => m.Rank)
            .ThenByDescending(m => m.Waypoint.ElevationMeters ?? double.MinValue)
            .ThenBy(m => m.Waypoint.Name, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .Select(m => m.Waypoint)
            .ToList();
    }

    // Lower-cases and strips diacritics so search is accent-insensitive. NFD decomposition + dropping
    // combining marks handles ś/č/ž/é…; ł/Ł do NOT decompose (they are not a base+mark), so map them
    // explicitly. Slovak ô/ŕ and Polish ó/ż all reduce to their base letter.
    private static string Fold(string s)
    {
        string lowered = s.Trim().ToLower(CultureInfo.InvariantCulture).Replace('ł', 'l');
        string decomposed = lowered.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (char c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}