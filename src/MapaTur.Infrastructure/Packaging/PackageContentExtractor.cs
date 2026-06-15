using System.IO.Compression;

using MapaTur.Application.Packaging;

namespace MapaTur.Infrastructure.Packaging;

/// <summary>
/// Installs a verified package archive into the exact paths the renderer already reads, so a download
/// changes nothing about rendering — only fills the data directories the auto-loader / tile cache scan:
/// <list type="bullet">
/// <item><description>DEM zip (<see cref="PackageLayer.Dem"/> + <see cref="PackageFormat.ZipTileCache"/>): the
/// <c>{z}/{x}/{y}.tif</c> tree is merged into the GUGiK DEM cache directory, overwriting older tiles.</description></item>
/// <item><description>Ortho zip (<see cref="PackageLayer.Ortho"/> + <see cref="PackageFormat.ZipTileCache"/>): the
/// <c>tatry-ortho-r{R}-c{C}.png</c> tiles are extracted into the maps directory, where the 3D ortho
/// auto-discovery finds them (it matches that filename pattern).</description></item>
/// <item><description>Base DEM zip (<see cref="PackageLayer.BaseDem"/> + <see cref="PackageFormat.ZipTileCache"/>):
/// the monolithic <c>tatry.dem</c> is extracted into the <c>dem/</c> folder, where the auto-loader finds it as
/// <c>*.dem</c> — this is what lets a freshly-installed app land in 3D.</description></item>
/// <item><description>MBTiles (<see cref="PackageFormat.MBTiles"/>): the file is moved into the maps directory
/// as <c>{id}.mbtiles</c> for the 2D base map.</description></item>
/// </list>
/// </summary>
public sealed class PackageContentExtractor : IPackageContentExtractor
{
    private readonly string demCacheDirectory;
    private readonly string mapsDirectory;
    private readonly string baseDemDirectory;

    /// <summary>Initializes the extractor with the renderer's data directories.</summary>
    /// <param name="demCacheDirectory">GUGiK DEM cache root (e.g. <c>{AppData}/dem-cache/gugik</c>).</param>
    /// <param name="mapsDirectory">Directory the map auto-loader scans for <c>.mbtiles</c> / ortho tiles.</param>
    /// <param name="baseDemDirectory">Directory the auto-loader scans for the monolithic base <c>*.dem</c>
    /// (e.g. <c>{AppData}/dem</c>).</param>
    public PackageContentExtractor(string demCacheDirectory, string mapsDirectory, string baseDemDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(demCacheDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(mapsDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDemDirectory);
        this.demCacheDirectory = demCacheDirectory;
        this.mapsDirectory = mapsDirectory;
        this.baseDemDirectory = baseDemDirectory;
    }

    /// <inheritdoc />
    public Task ExtractAsync(string archivePath, RegionPackage package, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentNullException.ThrowIfNull(package);
        cancellationToken.ThrowIfCancellationRequested();

        switch (package.Format)
        {
            case PackageFormat.ZipTileCache:
                // DEM tiles → elevation cache; the base .dem → dem/ folder; ortho PNG tiles → maps/ (3D drape).
                string zipTarget = package.Layer switch
                {
                    PackageLayer.Dem => this.demCacheDirectory,
                    PackageLayer.BaseDem => this.baseDemDirectory,
                    _ => this.mapsDirectory,
                };
                Directory.CreateDirectory(zipTarget);
                ZipFile.ExtractToDirectory(archivePath, zipTarget, overwriteFiles: true);
                break;

            case PackageFormat.MBTiles:
                Directory.CreateDirectory(this.mapsDirectory);
                string destination = Path.Combine(this.mapsDirectory, package.Id + ".mbtiles");
                File.Move(archivePath, destination, overwrite: true);
                break;

            default:
                throw new NotSupportedException($"Unsupported package format '{package.Format}'.");
        }

        return Task.CompletedTask;
    }
}