using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

using MapaTur.Application.Packaging;

return Baker.Run(args);

internal static class Baker
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            PrintUsage();
            return 1;
        }

        string command = args[0];
        string source = args[1];
        Dictionary<string, string> opts = ParseOptions(args.Skip(2));

        try
        {
            return command switch
            {
                "pack-dir" => PackDir(source, opts),
                "pack-file" => PackFile(source, opts),
                _ => Fail($"Unknown command '{command}'."),
            };
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    private static int PackDir(string sourceDir, Dictionary<string, string> o)
    {
        if (!Directory.Exists(sourceDir))
        {
            return Fail($"Source directory not found: {sourceDir}");
        }

        string id = Required(o, "id");
        int version = ParseInt(Required(o, "version"), "version");
        string outDir = Required(o, "out");
        string baseUrl = Required(o, "base-url").TrimEnd('/');
        string packagesDir = Path.Combine(outDir, "packages");
        Directory.CreateDirectory(packagesDir);

        string fileName = $"{id}-v{version}.zip";
        string zipPath = Path.Combine(packagesDir, fileName);
        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        Console.WriteLine($"Zipping {sourceDir} -> {zipPath} (this can take a while for the 1 m DEM) ...");
        ZipFile.CreateFromDirectory(sourceDir, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);

        RegionPackage pkg = BuildPackage(
            id, Required(o, "name"), ParseLayer(Required(o, "layer")),
            PackageFormat.ZipTileCache, version, zipPath, baseUrl, fileName);
        UpsertManifest(outDir, pkg);
        PrintPackage(pkg);
        return 0;
    }

    private static int PackFile(string sourceFile, Dictionary<string, string> o)
    {
        if (!File.Exists(sourceFile))
        {
            return Fail($"Source file not found: {sourceFile}");
        }

        string id = Required(o, "id");
        int version = ParseInt(Required(o, "version"), "version");
        string outDir = Required(o, "out");
        string baseUrl = Required(o, "base-url").TrimEnd('/');
        string ext = Path.GetExtension(sourceFile);
        PackageFormat format = o.TryGetValue("format", out string? f)
            ? ParseFormat(f)
            : ext.Equals(".mbtiles", StringComparison.OrdinalIgnoreCase) ? PackageFormat.MBTiles : PackageFormat.ZipTileCache;

        string packagesDir = Path.Combine(outDir, "packages");
        Directory.CreateDirectory(packagesDir);
        string fileName = $"{id}-v{version}{ext}";
        string destPath = Path.Combine(packagesDir, fileName);

        Console.WriteLine($"Copying {sourceFile} -> {destPath} ...");
        File.Copy(sourceFile, destPath, overwrite: true);

        RegionPackage pkg = BuildPackage(
            id, Required(o, "name"), ParseLayer(Required(o, "layer")),
            format, version, destPath, baseUrl, fileName);
        UpsertManifest(outDir, pkg);
        PrintPackage(pkg);
        return 0;
    }

    private static RegionPackage BuildPackage(
        string id, string name, PackageLayer layer, PackageFormat format,
        int version, string filePath, string baseUrl, string fileName)
    {
        long size = new FileInfo(filePath).Length;
        string sha = Sha256File(filePath);
        string url = $"{baseUrl}/packages/{fileName}";
        return new RegionPackage(id, name, layer, format, version, size, sha, url);
    }

    private static void UpsertManifest(string outDir, RegionPackage pkg)
    {
        Directory.CreateDirectory(outDir);
        string manifestPath = Path.Combine(outDir, "manifest.json");
        var packages = new List<RegionPackage>();
        if (File.Exists(manifestPath))
        {
            PackageManifest existing = PackageManifestParser.Parse(File.ReadAllBytes(manifestPath));
            packages.AddRange(existing.Packages.Where(p => p.Id != pkg.Id));
        }

        packages.Add(pkg);
        packages.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));

        string json = JsonSerializer.Serialize(new { packages }, WriteOptions);
        File.WriteAllText(manifestPath, json);
        Console.WriteLine($"Manifest updated: {manifestPath} ({packages.Count} package(s))");
    }

    private static string Sha256File(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static Dictionary<string, string> ParseOptions(IEnumerable<string> args)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string[] a = args.ToArray();
        for (int i = 0; i < a.Length; i++)
        {
            if (!a[i].StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            string key = a[i][2..];
            string value = i + 1 < a.Length && !a[i + 1].StartsWith("--", StringComparison.Ordinal) ? a[++i] : "true";
            dict[key] = value;
        }

        return dict;
    }

    private static string Required(Dictionary<string, string> o, string key) =>
        o.TryGetValue(key, out string? v) ? v : throw new ArgumentException($"Missing required option --{key}.");

    private static int ParseInt(string value, string name) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
            ? n
            : throw new ArgumentException($"--{name} must be an integer (got '{value}').");

    private static PackageLayer ParseLayer(string s) => Enum.Parse<PackageLayer>(s, ignoreCase: true);

    private static PackageFormat ParseFormat(string s) => Enum.Parse<PackageFormat>(s, ignoreCase: true);

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"ERROR: {message}");
        return 1;
    }

    private static void PrintPackage(RegionPackage p)
    {
        Console.WriteLine($"  id={p.Id}  v{p.Version}  layer={p.Layer}  format={p.Format}");
        Console.WriteLine($"  size={p.SizeBytes / 1_000_000.0:F1} MB  sha256={p.Sha256[..Math.Min(12, p.Sha256.Length)]}…");
        Console.WriteLine($"  url={p.Url}");
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
        PackageBaker — bake on-disk data into versioned packages + manifest.json

        Usage:
          PackageBaker pack-dir  <sourceDir>  --id <id> --name <name> --layer <Dem|Ortho> --version <N> --out <dataDir> --base-url <url>
          PackageBaker pack-file <sourceFile> --id <id> --name <name> --layer <Dem|Ortho> --version <N> --out <dataDir> --base-url <url> [--format <ZipTileCache|MBTiles>]

        Examples:
          # DEM 1 m: zip the app's GUGiK tile cache into a package
          PackageBaker pack-dir "%LOCALAPPDATA%\\...\\dem-cache\\gugik" --id tatry-dem-1m --name "Tatry — detal 1 m" --layer Dem --version 1 --out ./srv --base-url https://mapatur.up.railway.app

          # Ortho: package a prebuilt .mbtiles
          PackageBaker pack-file ./tatry-ortho.mbtiles --id tatry-ortho --name "Tatry — ortofoto" --layer Ortho --version 1 --out ./srv --base-url https://mapatur.up.railway.app

        The --out dir ends up as: {out}/manifest.json + {out}/packages/<files>  — copy it to the PackageServer's PACKAGES_DIR.
        """);
    }
}
