namespace MapaTur.Application.Packaging;

/// <summary>The data layer a region package fills.</summary>
public enum PackageLayer
{
    /// <summary>Elevation tiles (GUGiK 1 m + base zooms) shipped as a zip of the DEM tile-cache tree.</summary>
    Dem,

    /// <summary>Orthophoto / raster map imagery shipped as a single MBTiles SQLite container.</summary>
    Ortho,

    /// <summary>The monolithic base terrain DEM (e.g. <c>tatry.dem</c>) shipped as a zip; unpacked into the
    /// <c>dem/</c> data folder the auto-loader scans for <c>*.dem</c>. This is what makes a fresh install land
    /// in 3D — without it the app falls back to the flat 2D map.</summary>
    BaseDem,
}

/// <summary>How a package's bytes are laid out, which decides how it is installed on the device.</summary>
public enum PackageFormat
{
    /// <summary>A ZIP of a <c>{z}/{x}/{y}.tif</c> tree, extracted into the DEM cache directory.</summary>
    ZipTileCache,

    /// <summary>A single <c>.mbtiles</c> file, moved into the maps directory and mounted read-only.</summary>
    MBTiles,
}

/// <summary>
/// One downloadable region data package as advertised by the server manifest. Immutable description only —
/// the bytes live at <see cref="Url"/>; <see cref="Sha256"/> verifies them after download and
/// <see cref="Version"/> drives update detection against whatever is already installed.
/// </summary>
/// <param name="Id">Stable identifier (e.g. <c>tatry-dem-1m</c>); also the on-disk marker key.</param>
/// <param name="Name">Human-facing label shown in the download list.</param>
/// <param name="Layer">Which renderer layer the package feeds.</param>
/// <param name="Format">Wire layout, which selects the installer path.</param>
/// <param name="Version">Monotonic data version; a higher value than the installed marker means "update".</param>
/// <param name="SizeBytes">Download size for the UI estimate and the progress total.</param>
/// <param name="Sha256">Lower-case hex SHA-256 of the package bytes, verified before install.</param>
/// <param name="Url">Absolute URL the bytes are fetched from (Railway today, swappable to a CDN later).</param>
public sealed record RegionPackage(
    string Id,
    string Name,
    PackageLayer Layer,
    PackageFormat Format,
    int Version,
    long SizeBytes,
    string Sha256,
    string Url);

/// <summary>The server's catalogue of available <see cref="RegionPackage"/>s.</summary>
/// <param name="Packages">Advertised packages, in display order.</param>
public sealed record PackageManifest(IReadOnlyList<RegionPackage> Packages);

/// <summary>A package recorded as installed on disk (its marker), used to detect available updates.</summary>
/// <param name="Id">The <see cref="RegionPackage.Id"/> that was installed.</param>
/// <param name="Version">The <see cref="RegionPackage.Version"/> that was installed.</param>
/// <param name="Sha256">The verified hash that was installed (lets a future check spot a re-baked same-version).</param>
public sealed record InstalledPackage(string Id, int Version, string Sha256);

/// <summary>Install state of a package relative to what the manifest now advertises.</summary>
public enum PackageState
{
    /// <summary>No marker on disk for this id.</summary>
    NotInstalled,

    /// <summary>Installed at the advertised version (or newer).</summary>
    Installed,

    /// <summary>Installed, but the manifest advertises a newer <see cref="RegionPackage.Version"/>.</summary>
    UpdateAvailable,
}

/// <summary>A manifest package paired with its current on-device <see cref="State"/>.</summary>
/// <param name="Package">The advertised package.</param>
/// <param name="State">Where it stands relative to the installed marker.</param>
public sealed record PackageStatus(RegionPackage Package, PackageState State);

/// <summary>Download progress for a package: <see cref="BytesReceived"/> of <see cref="TotalBytes"/>.</summary>
public readonly record struct PackageDownloadProgress(long BytesReceived, long TotalBytes);

/// <summary>Outcome of installing a package.</summary>
/// <param name="Id">The installed package id.</param>
/// <param name="BytesDownloaded">Bytes pulled over the network this run (0 if fully resumed/cached).</param>
/// <param name="ResumedFromPartial">True when a leftover partial file let the download resume mid-stream.</param>
public readonly record struct PackageInstallResult(string Id, long BytesDownloaded, bool ResumedFromPartial);

/// <summary>Fetches package bytes over the network with HTTP range support so a dropped download resumes.</summary>
public interface IPackageFileFetcher
{
    /// <summary>Total length of the resource in bytes (HTTP Content-Length).</summary>
    Task<long> GetContentLengthAsync(string url, CancellationToken cancellationToken = default);

    /// <summary>Opens a read stream over the resource starting at <paramref name="fromByte"/> (a Range GET).</summary>
    Task<Stream> OpenRangeAsync(string url, long fromByte, CancellationToken cancellationToken = default);
}

/// <summary>Installs a fully-downloaded, hash-verified package archive into its on-disk home.</summary>
public interface IPackageContentExtractor
{
    /// <summary>
    /// Places the archive at <paramref name="archivePath"/> for <paramref name="package"/>: a DEM zip is
    /// extracted into the elevation cache tree; an MBTiles file is moved into the maps directory.
    /// </summary>
    Task ExtractAsync(string archivePath, RegionPackage package, CancellationToken cancellationToken = default);
}

/// <summary>Persists which packages (and versions) are installed so the catalogue can flag updates.</summary>
public interface IInstalledPackageStore
{
    /// <summary>All packages currently recorded as installed.</summary>
    IReadOnlyCollection<InstalledPackage> List();

    /// <summary>Records (or overwrites) the installed marker for one package.</summary>
    void MarkInstalled(InstalledPackage package);

    /// <summary>Removes the installed marker for <paramref name="id"/> (no-op if absent).</summary>
    void Remove(string id);
}

/// <summary>Fetches the server's package catalogue (<c>manifest.json</c>).</summary>
public interface IPackageCatalogSource
{
    /// <summary>Downloads and parses the current manifest.</summary>
    Task<PackageManifest> GetManifestAsync(CancellationToken cancellationToken = default);
}

/// <summary>Downloads, verifies and installs a single package (resuming any partial download).</summary>
public interface IPackageInstaller
{
    /// <summary>Installs <paramref name="package"/>, reporting byte-level progress.</summary>
    Task<PackageInstallResult> InstallAsync(
        RegionPackage package,
        IProgress<PackageDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}