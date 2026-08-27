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
}