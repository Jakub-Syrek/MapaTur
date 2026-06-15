using System.IO.Compression;
using System.Text;

using FluentAssertions;

using MapaTur.Application.Packaging;
using MapaTur.Infrastructure.Packaging;

namespace MapaTur.Infrastructure.Tests.Packaging;

public sealed class PackageContentExtractorTests : IDisposable
{
    private readonly string root;
    private readonly string demCacheDir;
    private readonly string mapsDir;
    private readonly string baseDemDir;
    private readonly string workDir;

    public PackageContentExtractorTests()
    {
        root = Path.Combine(Path.GetTempPath(), "mapatur-extract-tests", Guid.NewGuid().ToString("N"));
        demCacheDir = Path.Combine(root, "dem-cache", "gugik");
        mapsDir = Path.Combine(root, "maps");
        baseDemDir = Path.Combine(root, "dem");
        workDir = Path.Combine(root, "work");
        Directory.CreateDirectory(demCacheDir);
        Directory.CreateDirectory(mapsDir);
        Directory.CreateDirectory(baseDemDir);
        Directory.CreateDirectory(workDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    private PackageContentExtractor NewExtractor() => new(demCacheDir, mapsDir, baseDemDir);

    private string MakeZip(string name, params (string entry, string content)[] entries)
    {
        string path = Path.Combine(workDir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach ((string entry, string content) in entries)
        {
            ZipArchiveEntry archiveEntry = zip.CreateEntry(entry);
            using var writer = new StreamWriter(archiveEntry.Open(), Encoding.UTF8);
            writer.Write(content);
        }

        return path;
    }

    private static RegionPackage DemPkg() =>
        new("tatry-dem-1m", "DEM", PackageLayer.Dem, PackageFormat.ZipTileCache, 1, 1, "00", "https://x/dem.zip");

    private static RegionPackage OrthoPkg() =>
        new("tatry-ortho", "Ortho", PackageLayer.Ortho, PackageFormat.MBTiles, 1, 1, "00", "https://x/ortho.mbtiles");

    private static RegionPackage BaseDemPkg() =>
        new("tatry-dem-base", "Baza", PackageLayer.BaseDem, PackageFormat.ZipTileCache, 1, 1, "00", "https://x/base.zip");

    [Fact]
    public async Task ExtractAsync_Dem_UnzipsTileTreeIntoCacheDir()
    {
        string archive = MakeZip("dem.part", ("16/1/2.tif", "ELEV"));

        await NewExtractor().ExtractAsync(archive, DemPkg());

        string tile = Path.Combine(demCacheDir, "16", "1", "2.tif");
        File.Exists(tile).Should().BeTrue();
        (await File.ReadAllTextAsync(tile)).Should().Be("ELEV");
    }

    [Fact]
    public async Task ExtractAsync_BaseDem_UnzipsDemFileIntoDemDir()
    {
        // The base terrain DEM (tatry.dem) must land in dem/ where the auto-loader finds *.dem → 3D on first run.
        string archive = MakeZip("base.part", ("tatry.dem", "HEIGHTFIELD"));

        await NewExtractor().ExtractAsync(archive, BaseDemPkg());

        string dem = Path.Combine(baseDemDir, "tatry.dem");
        File.Exists(dem).Should().BeTrue();
        (await File.ReadAllTextAsync(dem)).Should().Be("HEIGHTFIELD");
        // It must NOT pollute the DEM tile cache or the maps dir.
        Directory.GetFiles(demCacheDir, "*", SearchOption.AllDirectories).Should().BeEmpty();
        Directory.GetFiles(mapsDir, "*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_Dem_OverwritesAnExistingTile()
    {
        string existing = Path.Combine(demCacheDir, "16", "1", "2.tif");
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        await File.WriteAllTextAsync(existing, "OLD");
        string archive = MakeZip("dem.part", ("16/1/2.tif", "NEW"));

        await NewExtractor().ExtractAsync(archive, DemPkg());

        (await File.ReadAllTextAsync(existing)).Should().Be("NEW");
    }

    [Fact]
    public async Task ExtractAsync_OrthoZip_UnzipsPngTilesIntoMapsDir()
    {
        // The 3D ortho drape is discovered as tatry-ortho-r{R}-c{C}.png in a maps search root.
        string archive = MakeZip("ortho.part", ("tatry-ortho-r0-c0.png", "PNG0"), ("tatry-ortho-r0-c1.png", "PNG1"));
        var orthoZip = new RegionPackage(
            "tatry-ortho", "Ortho", PackageLayer.Ortho, PackageFormat.ZipTileCache, 1, 1, "00", "https://x/ortho.zip");

        await NewExtractor().ExtractAsync(archive, orthoZip);

        File.Exists(Path.Combine(mapsDir, "tatry-ortho-r0-c0.png")).Should().BeTrue();
        File.Exists(Path.Combine(mapsDir, "tatry-ortho-r0-c1.png")).Should().BeTrue();
        // A DEM cache stays untouched by an ortho install.
        Directory.GetFiles(demCacheDir, "*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_Ortho_PlacesMbtilesFileInMapsDir()
    {
        string archive = Path.Combine(workDir, "ortho.part");
        await File.WriteAllTextAsync(archive, "SQLITE-BYTES");

        await NewExtractor().ExtractAsync(archive, OrthoPkg());

        string installed = Path.Combine(mapsDir, "tatry-ortho.mbtiles");
        File.Exists(installed).Should().BeTrue();
        (await File.ReadAllTextAsync(installed)).Should().Be("SQLITE-BYTES");
    }

    [Fact]
    public async Task ExtractAsync_Ortho_OverwritesAPreviousMbtiles()
    {
        string installed = Path.Combine(mapsDir, "tatry-ortho.mbtiles");
        await File.WriteAllTextAsync(installed, "OLD");
        string archive = Path.Combine(workDir, "ortho.part");
        await File.WriteAllTextAsync(archive, "NEW");

        await NewExtractor().ExtractAsync(archive, OrthoPkg());

        (await File.ReadAllTextAsync(installed)).Should().Be("NEW");
    }
}