namespace MapaTur.Application.Packaging;

/// <summary>
/// Merges the server <see cref="PackageManifest"/> with the on-device installed markers into the list the
/// UI shows: every advertised package paired with its <see cref="PackageState"/>. The manifest drives the
/// list — stale markers for withdrawn packages are dropped, and an installed version at or above the
/// advertised one counts as <see cref="PackageState.Installed"/> (never nags for a downgrade).
/// </summary>
public static class PackageCatalog
{
    /// <summary>Pairs each advertised package with its install state, in manifest order.</summary>
    /// <param name="manifest">The server's advertised packages.</param>
    /// <param name="installed">Markers for what is currently installed on disk.</param>
    public static IReadOnlyList<PackageStatus> Merge(
        PackageManifest manifest, IReadOnlyCollection<InstalledPackage> installed)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(installed);

        var byId = new Dictionary<string, InstalledPackage>(StringComparer.Ordinal);
        foreach (InstalledPackage marker in installed)
        {
            byId[marker.Id] = marker;
        }

        var result = new List<PackageStatus>(manifest.Packages.Count);
        foreach (RegionPackage package in manifest.Packages)
        {
            PackageState state = byId.TryGetValue(package.Id, out InstalledPackage? marker)
                ? (marker.Version < package.Version ? PackageState.UpdateAvailable : PackageState.Installed)
                : PackageState.NotInstalled;
            result.Add(new PackageStatus(package, state));
        }

        return result;
    }
}