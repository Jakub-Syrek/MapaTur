namespace MapaTur.Application.Maps;

/// <summary>
/// Per-region pick among discovered data files (P-A2). Today's installs have a single region's files,
/// so the fallback keeps the auto-loader's historical "first found" behaviour bit-for-bit; once a
/// second region's files land next to them (zermatt.dem beside tatry.dem), the active region's name
/// decides instead of directory enumeration order.
/// </summary>
public static class RegionFileSelection
{
    /// <summary>
    /// The `.dem` whose file name matches <paramref name="regionId"/> (case-insensitive), or the first
    /// candidate when none matches, or null when the list is empty.
    /// </summary>
    public static string? PickDem(IEnumerable<string> candidates, string regionId)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentException.ThrowIfNullOrWhiteSpace(regionId);

        string? first = null;
        foreach (string path in candidates)
        {
            first ??= path;
            if (string.Equals(Path.GetFileNameWithoutExtension(path), regionId, StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }
        }

        return first;
    }

    /// <summary>
    /// Filters ortho candidates for the active region: a file whose name starts with a REGISTERED
    /// region id prefix ("{id}-") belongs to that region — other regions' files are excluded (the
    /// first Zermatt light, 2026-08-27, draped the Tatra ortho set over Swiss terrain because the
    /// sharpest-set contest was region-blind). Files matching NO registered region keep the historical
    /// behaviour (custom-named installs stay draped).
    /// </summary>
    public static IReadOnlyList<string> FilterOrtho(
        IEnumerable<string> candidates, string activeRegionId, IReadOnlyList<string> allRegionIds)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentException.ThrowIfNullOrWhiteSpace(activeRegionId);
        ArgumentNullException.ThrowIfNull(allRegionIds);

        var kept = new List<string>();
        foreach (string path in candidates)
        {
            string name = Path.GetFileName(path);
            string? owner = null;
            foreach (string id in allRegionIds)
            {
                if (name.StartsWith(id + "-", StringComparison.OrdinalIgnoreCase))
                {
                    owner = id;
                    break;
                }
            }

            if (owner is null || string.Equals(owner, activeRegionId, StringComparison.OrdinalIgnoreCase))
            {
                kept.Add(path);
            }
        }

        return kept;
    }
}