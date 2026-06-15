namespace MapaTur.Application.Packaging;

/// <summary>
/// Use-case facade the UI talks to: combines the catalogue source, the installed-marker store and the
/// installer so the view model just asks for "the list" and "install this one". Keeps the merge logic
/// (<see cref="PackageCatalog"/>) and the install pipeline (<see cref="IPackageInstaller"/>) behind one port.
/// </summary>
public sealed class OfflinePackageService
{
    private readonly IPackageCatalogSource catalogSource;
    private readonly IInstalledPackageStore store;
    private readonly IPackageInstaller installer;

    /// <summary>Initializes the service.</summary>
    /// <param name="catalogSource">Fetches the server manifest.</param>
    /// <param name="store">Reads which packages are installed.</param>
    /// <param name="installer">Downloads and installs a package.</param>
    public OfflinePackageService(
        IPackageCatalogSource catalogSource, IInstalledPackageStore store, IPackageInstaller installer)
    {
        ArgumentNullException.ThrowIfNull(catalogSource);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(installer);

        this.catalogSource = catalogSource;
        this.store = store;
        this.installer = installer;
    }

    /// <summary>Fetches the manifest and pairs every advertised package with its install state.</summary>
    public async Task<IReadOnlyList<PackageStatus>> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        PackageManifest manifest = await this.catalogSource.GetManifestAsync(cancellationToken).ConfigureAwait(false);
        return PackageCatalog.Merge(manifest, this.store.List());
    }

    /// <summary>Installs one package, resuming any partial download.</summary>
    public Task<PackageInstallResult> InstallAsync(
        RegionPackage package,
        IProgress<PackageDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        this.installer.InstallAsync(package, progress, cancellationToken);
}