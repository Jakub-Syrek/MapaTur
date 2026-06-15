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
/// <item><description>MBTiles (<see cref="PackageFormat.MBTiles"/>): the file is moved into the maps directory
/// as <c>{id}.mbtiles</c> for the 2D base map.</description></item>
/// </list>
/// </summary>
public sealed class PackageContentExtractor : IPackageContentExtractor
{
    private readonly string demCacheDirectory;
    private readonly string mapsDirectory;

    /// <summary>Initializes the extractor with the renderer's data directories.</summary>
    /// <param name="demCacheDirectory">GUGiK DEM cache root (e.g. <c>{AppData}/dem-cache/gugik</c>).</param>
    /// <param name="mapsDirectory">Directory the map auto-loader scans for <c>.mbtiles</c> files.</param>
    public PackageContentExtractor(string demCacheDirectory, string mapsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(demCacheDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(mapsDirectory);
        this.demCacheDirectory = demCacheDirectory;
        this.mapsDirectory = mapsDirectory;
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
                // DEM tiles fill the elevation cache; ortho PNG tiles fill the maps dir the 3D drape scans.
                string zipTarget = package.Layer == PackageLayer.Dem ? this.demCacheDirectory : this.mapsDirectory;
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