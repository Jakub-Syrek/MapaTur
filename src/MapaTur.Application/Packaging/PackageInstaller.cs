using System.Security.Cryptography;

namespace MapaTur.Application.Packaging;

/// <summary>
/// Downloads, verifies and installs a <see cref="RegionPackage"/>. The bytes stream to a
/// <c>{id}.part</c> file in the work directory and the download <b>resumes</b> from whatever is already
/// there (HTTP range), so a dropped connection in the field just re-runs and continues. Once the full file
/// matches the manifest's SHA-256 it is handed to an <see cref="IPackageContentExtractor"/> (DEM zip → cache
/// tree, ortho → maps dir) and recorded in the <see cref="IInstalledPackageStore"/>. A hash mismatch discards
/// the partial and throws, so a retry re-downloads cleanly rather than installing corruption.
/// </summary>
public sealed class PackageInstaller : IPackageInstaller
{
    private const int CopyBufferSize = 64 * 1024;

    private readonly IPackageFileFetcher fetcher;
    private readonly IPackageContentExtractor extractor;
    private readonly IInstalledPackageStore store;
    private readonly string workDirectory;

    /// <summary>Initializes the installer.</summary>
    /// <param name="fetcher">Network fetcher with range support (for resume).</param>
    /// <param name="extractor">Places the verified archive into its on-disk home.</param>
    /// <param name="store">Records installed markers so the catalogue can detect updates.</param>
    /// <param name="workDirectory">Directory holding in-flight <c>.part</c> downloads.</param>
    public PackageInstaller(
        IPackageFileFetcher fetcher,
        IPackageContentExtractor extractor,
        IInstalledPackageStore store,
        string workDirectory)
    {
        ArgumentNullException.ThrowIfNull(fetcher);
        ArgumentNullException.ThrowIfNull(extractor);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(workDirectory);

        this.fetcher = fetcher;
        this.extractor = extractor;
        this.store = store;
        this.workDirectory = workDirectory;
    }

    /// <summary>Installs <paramref name="package"/>, resuming any prior partial download.</summary>
    /// <param name="package">The package to install.</param>
    /// <param name="progress">Optional byte-level progress.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>What was downloaded this run and whether it resumed a partial.</returns>
    /// <exception cref="InvalidDataException">The downloaded bytes do not match <see cref="RegionPackage.Sha256"/>.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public async Task<PackageInstallResult> InstallAsync(
        RegionPackage package,
        IProgress<PackageDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(this.workDirectory);
        string partialPath = Path.Combine(this.workDirectory, package.Id + ".part");

        long total = await this.fetcher.GetContentLengthAsync(package.Url, cancellationToken).ConfigureAwait(false);
        long have = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        if (have > total)
        {
            // A partial larger than the resource means a stale/changed file — discard and start over.
            TryDelete(partialPath);
            have = 0;
        }

        bool resumed = have > 0;
        long downloaded = 0;
        progress?.Report(new PackageDownloadProgress(have, total));

        if (have < total)
        {
            await using Stream source =
                await this.fetcher.OpenRangeAsync(package.Url, have, cancellationToken).ConfigureAwait(false);
            await using var destination = new FileStream(
                partialPath, FileMode.Append, FileAccess.Write, FileShare.None, CopyBufferSize, useAsync: true);

            var buffer = new byte[CopyBufferSize];
            long position = have;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                position += read;
                downloaded += read;
                progress?.Report(new PackageDownloadProgress(position, total));
            }
        }

        await VerifyHashAsync(partialPath, package, cancellationToken).ConfigureAwait(false);

        await this.extractor.ExtractAsync(partialPath, package, cancellationToken).ConfigureAwait(false);
        this.store.MarkInstalled(new InstalledPackage(package.Id, package.Version, package.Sha256));

        TryDelete(partialPath);
        return new PackageInstallResult(package.Id, downloaded, resumed);
    }

    private static async Task VerifyHashAsync(string path, RegionPackage package, CancellationToken cancellationToken)
    {
        string actual;
        await using (var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, useAsync: true))
        {
            byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            actual = Convert.ToHexString(hash).ToLowerInvariant();
        }

        if (!string.Equals(actual, package.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(path);
            throw new InvalidDataException(
                $"Package '{package.Id}' failed hash verification (expected {package.Sha256}, got {actual}).");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A leftover temp file is harmless; a later run overwrites or resumes it.
        }
    }
}