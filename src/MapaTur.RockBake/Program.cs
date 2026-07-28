using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.Json;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;
using MapaTur.Infrastructure.Terrain;

using SkiaSharp;

string? demPath = GetArgument("--dem");
string? baseDemPath = GetArgument("--base-dem");
string? heightArgument = GetArgument("--height");
string? outputRoot = GetArgument("--out");
if (demPath is null || baseDemPath is null || heightArgument is null || outputRoot is null)
{
    PrintUsage();
    return 2;
}

float featureMeters = ParseFloat(GetArgument("--feature") ?? "4.0", "--feature");
float amplitudeMeters = ParseFloat(GetArgument("--amplitude") ?? "0.55", "--amplitude");
float pageMeters = ParseFloat(GetArgument("--page") ?? "16.0", "--page");
bool continuousRmp2 = HasFlag("--continuous-rmp2");
bool neutralizeAlbedo = HasFlag("--neutralize-albedo");
bool flatAlbedo = HasFlag("--flat-albedo");
float maximumReliefMeters = ParseFloat(GetArgument("--maximum-relief") ?? "2.0", "--maximum-relief");
float maximumEdgeMeters = ParseFloat(GetArgument("--edge") ?? "0.65", "--edge");
int synthesisSeed = ParseInt(GetArgument("--seed") ?? "20260727", "--seed");
int materialResolution = ParseInt(GetArgument("--material-resolution") ?? "2048", "--material-resolution");
ushort materialPageId = checked((ushort)ParseInt(
    GetArgument("--material-page") ?? (continuousRmp2 ? "20" : "19"),
    "--material-page"));
string[] scanPaths = (GetArgument("--scan") ?? string.Empty)
    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
string[] reliefScanPaths = (GetArgument("--relief-scan") ?? string.Empty)
    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
byte[] lods = (GetArgument("--lod") ?? "0,1,2")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(value => byte.Parse(value, CultureInfo.InvariantCulture))
    .ToArray();
string[] heightPaths = heightArgument
    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

EnsureFile(demPath);
EnsureFile(baseDemPath);
foreach (string path in heightPaths)
{
    EnsureFile(path);
}

foreach (string path in scanPaths)
{
    EnsureFile(path);
}

foreach (string path in reliefScanPaths)
{
    EnsureFile(path);
}

if (continuousRmp2 && scanPaths.Length < 2)
{
    throw new ArgumentException("--continuous-rmp2 requires at least two glTF paths in --scan.");
}

if (Directory.Exists(outputRoot))
{
    throw new IOException($"Output already exists: {outputRoot}");
}

var stopwatch = Stopwatch.StartNew();
Console.WriteLine($"[rock-bake] DEM: {demPath}");
Console.WriteLine($"[rock-bake] scans: {heightPaths.Length}, feature={featureMeters:F2} m, amplitude={amplitudeMeters:F2} m");

BakedDemTile tile;
using (FileStream stream = File.OpenRead(demPath))
{
    tile = BakedDemTileStore.Read(stream);
}

DemRaster baseDem = DemRasterReader.Read(baseDemPath);
var anchor = new GeoPoint(
    (baseDem.North + baseDem.South) * 0.5,
    (baseDem.East + baseDem.West) * 0.5);
DemRaster tileRaster = BakedTileMeshBuilder.AsRaster(tile);
TerrainMesh3D mesh = TerrainMesh3D.Build(
    tileRaster,
    new TerrainMeshOptions
    {
        VerticalExaggeration = 1f,
        SkirtDepthMeters = 0f,
        NormalApronCells = 0,
    },
    anchor);

var sourceTriangles = new List<RockMeshTriangle>(mesh.Indices.Length / 3);
for (int i = 0; i < mesh.Indices.Length; i += 3)
{
    sourceTriangles.Add(new RockMeshTriangle(
        mesh.Vertices[mesh.Indices[i]],
        mesh.Vertices[mesh.Indices[i + 1]],
        mesh.Vertices[mesh.Indices[i + 2]]));
}

RockHeightMap[] heightMaps = heightPaths.Select(LoadHeightMap).ToArray();
var relief = new RockScanReliefSampler(heightMaps, featureMeters, amplitudeMeters);
if (continuousRmp2)
{
    return BakeContinuousRmp2(
        sourceTriangles,
        relief,
        scanPaths,
        outputRoot,
        pageMeters,
        amplitudeMeters,
        maximumReliefMeters,
        maximumEdgeMeters,
        synthesisSeed,
        materialResolution,
        materialPageId,
        neutralizeAlbedo,
        flatAlbedo,
        reliefScanPaths,
        featureMeters,
        tile,
        anchor,
        stopwatch);
}

IReadOnlyList<RockMeshPage> pages = RockMeshPageSetBaker.Bake(
    sourceTriangles,
    pageMeters,
    relief.Sample,
    lods);

string temporaryRoot = outputRoot + $".tmp-{Guid.NewGuid():N}";
try
{
    Directory.CreateDirectory(temporaryRoot);
    long vertexCount = 0;
    long triangleCount = 0;
    long payloadBytes = 0;
    foreach (RockMeshPage page in pages)
    {
        string relative = RockMeshPageStore.RelativePathFor(page.Lod, page.PageX, page.PageY);
        string path = Path.Combine(temporaryRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (FileStream stream = File.Create(path))
        {
            RockMeshPageStore.Write(stream, page);
        }

        using (FileStream stream = File.OpenRead(path))
        {
            RockMeshPage verified = RockMeshPageStore.Read(stream);
            if (verified.VertexCount != page.VertexCount || verified.IndexCount != page.IndexCount)
            {
                throw new InvalidDataException($"RMP verification failed for {relative}.");
            }
        }

        vertexCount += page.VertexCount;
        triangleCount += page.IndexCount / 3;
        payloadBytes += page.VertexData.LongLength + (page.Indices.LongLength * sizeof(ushort));
    }

    var manifest = new
    {
        format = "RMP1",
        source = new { tile.Zoom, tile.TileX, tile.TileY },
        anchor = new { anchor.Latitude, anchor.Longitude },
        pageMeters,
        featureMeters,
        amplitudeMeters,
        lods = lods.Select(value => (int)value).ToArray(),
        scans = heightPaths.Select(Path.GetFileName).ToArray(),
        pages = pages.Count,
        vertexCount,
        triangleCount,
        payloadBytes,
    };
    File.WriteAllText(
        Path.Combine(temporaryRoot, "manifest.json"),
        JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    Directory.Move(temporaryRoot, outputRoot);

    Console.WriteLine(
        $"[rock-bake] OK: pages={pages.Count}, vertices={vertexCount:N0}, triangles={triangleCount:N0}, "
        + $"payload={payloadBytes / (1024.0 * 1024.0):F1} MiB, time={stopwatch.Elapsed.TotalSeconds:F1}s");
    Console.WriteLine($"[rock-bake] output: {outputRoot}");
}
catch
{
    if (Directory.Exists(temporaryRoot))
    {
        Directory.Delete(temporaryRoot, recursive: true);
    }

    throw;
}

return 0;

string? GetArgument(string name)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

bool HasFlag(string name) => Array.IndexOf(args, name) >= 0;

static float ParseFloat(string value, string name)
{
    if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
        || !float.IsFinite(parsed)
        || parsed <= 0f)
    {
        throw new ArgumentException($"{name} must be a positive finite number.");
    }

    return parsed;
}

static int ParseInt(string value, string name)
{
    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
        || parsed <= 0)
    {
        throw new ArgumentException($"{name} must be a positive integer.");
    }

    return parsed;
}

static void EnsureFile(string path)
{
    if (!File.Exists(path))
    {
        throw new FileNotFoundException("Rock bake input does not exist.", path);
    }
}

static RockHeightMap LoadHeightMap(string path)
{
    using SKBitmap bitmap = SKBitmap.Decode(path)
        ?? throw new InvalidDataException($"Cannot decode scanned displacement map: {path}");
    var samples = new float[checked(bitmap.Width * bitmap.Height)];
    float minimum = 1f;
    float maximum = 0f;
    for (int y = 0; y < bitmap.Height; y++)
    {
        for (int x = 0; x < bitmap.Width; x++)
        {
            SKColor pixel = bitmap.GetPixel(x, y);
            float value = ((0.2126f * pixel.Red) + (0.7152f * pixel.Green) + (0.0722f * pixel.Blue)) / 255f;
            samples[(y * bitmap.Width) + x] = value;
            minimum = MathF.Min(minimum, value);
            maximum = MathF.Max(maximum, value);
        }
    }

    float range = maximum - minimum;
    if (range < 0.01f)
    {
        throw new InvalidDataException($"Scanned displacement map has no useful height range: {path}");
    }

    for (int i = 0; i < samples.Length; i++)
    {
        samples[i] = (samples[i] - minimum) / range;
    }

    Console.WriteLine($"[rock-bake] scan {Path.GetFileName(path)}: {bitmap.Width}x{bitmap.Height}");
    return new RockHeightMap(bitmap.Width, bitmap.Height, samples);
}

static int BakeContinuousRmp2(
    IReadOnlyList<RockMeshTriangle> sourceTriangles,
    RockScanReliefSampler relief,
    IReadOnlyList<string> scanPaths,
    string outputRoot,
    float pageMeters,
    float sampleAmplitudeMeters,
    float maximumReliefMeters,
    float maximumEdgeMeters,
    int synthesisSeed,
    int materialResolution,
    ushort materialPageId,
    bool neutralizeAlbedo,
    bool flatAlbedo,
    IReadOnlyList<string> reliefScanPaths,
    float featureMeters,
    BakedDemTile tile,
    GeoPoint anchor,
    Stopwatch stopwatch)
{
    Console.WriteLine(
        $"[rock-bake] continuous RMP2: max relief={maximumReliefMeters:F2} m, "
        + $"edge={maximumEdgeMeters:F2} m, seed={synthesisSeed}");
    PhotogrammetryRockPrimitive[] scanPrimitives = scanPaths
        .Select(path => PhotogrammetryRockAsset.Load(path).Primitives[0])
        .ToArray();
    if (reliefScanPaths.Count > 0)
    {
        RockHeightMap[] scanGeometryRelief = reliefScanPaths
            .Select(path => PhotogrammetryReliefMapExtractor.Extract(
                PhotogrammetryRockAsset.Load(path).Primitives[0],
                width: 512,
                height: 512))
            .ToArray();
        relief = new RockScanReliefSampler(
            scanGeometryRelief,
            featureMeters,
            sampleAmplitudeMeters);
        Console.WriteLine(
            $"[rock-bake] relief extracted from {scanGeometryRelief.Length} real scan meshes at 512x512");
    }

    RockAlbedoTile[] albedos = scanPrimitives
        .Select(DecodeAlbedo)
        .ToArray();
    byte[] synthesizedAlbedo = RockAlbedoSynthesizer.Synthesize(
        albedos,
        materialResolution,
        materialResolution,
        synthesisSeed);
    if (neutralizeAlbedo)
    {
        synthesizedAlbedo = RockAlbedoNeutralizer.Neutralize(synthesizedAlbedo);
    }

    if (flatAlbedo)
    {
        for (int pixel = 0; pixel < synthesizedAlbedo.Length; pixel += 4)
        {
            synthesizedAlbedo[pixel] = 88;
            synthesizedAlbedo[pixel + 1] = 92;
            synthesizedAlbedo[pixel + 2] = 96;
            synthesizedAlbedo[pixel + 3] = byte.MaxValue;
        }
    }
    PhotogrammetryRockPrimitive surface = ContinuousScannedRockSurfaceBuilder.Build(
        sourceTriangles,
        relief.Sample,
        sampleAmplitudeMeters,
        maximumReliefMeters,
        maximumEdgeMeters,
        synthesisSeed,
        baseColorImageBytes: null);
    IReadOnlyList<ScannedRockMeshPage> pages = ScannedRockPageBaker.Bake(
        surface,
        pageMeters,
        lod: 0,
        geometricError: maximumReliefMeters + maximumEdgeMeters,
        materialPageId);
    RockMaterialPage material = RockMaterialPageBaker.Bake(
        materialPageId,
        synthesizedAlbedo,
        materialResolution,
        materialResolution);

    string temporaryRoot = outputRoot + $".tmp-{Guid.NewGuid():N}";
    try
    {
        Directory.CreateDirectory(temporaryRoot);
        long vertexCount = 0;
        long triangleCount = 0;
        long geometryBytes = 0;
        foreach (ScannedRockMeshPage page in pages)
        {
            string relative = ScannedRockMeshPageStore.RelativePathFor(page.Lod, page.PageX, page.PageY);
            string path = Path.Combine(temporaryRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (FileStream stream = File.Create(path))
            {
                ScannedRockMeshPageStore.Write(stream, page);
            }

            using (FileStream stream = File.OpenRead(path))
            {
                ScannedRockMeshPage verified = ScannedRockMeshPageStore.Read(stream);
                if (verified.VertexCount != page.VertexCount || verified.IndexCount != page.IndexCount)
                {
                    throw new InvalidDataException($"RMP2 verification failed for {relative}.");
                }
            }

            vertexCount += page.VertexCount;
            triangleCount += page.IndexCount / 3;
            geometryBytes += page.VertexData.LongLength + (page.Indices.LongLength * sizeof(ushort));
        }

        string materialPath = Path.Combine(temporaryRoot, $"{materialPageId}{RockMaterialPageStore.FileExtension}");
        using (FileStream stream = File.Create(materialPath))
        {
            RockMaterialPageStore.Write(stream, material);
        }

        using (FileStream stream = File.OpenRead(materialPath))
        {
            _ = RockMaterialPageStore.Read(stream);
        }

        var manifest = new
        {
            format = "RMP2+RTX1-continuous-dem",
            source = new { tile.Zoom, tile.TileX, tile.TileY },
            anchor = new { anchor.Latitude, anchor.Longitude },
            pageMeters,
            maximumEdgeMeters,
            sampleAmplitudeMeters,
            maximumReliefMeters,
            synthesisSeed,
            scans = scanPaths.Select(Path.GetFileName).ToArray(),
            reliefScans = reliefScanPaths.Select(Path.GetFileName).ToArray(),
            materialPageId,
            material = new
            {
                material.Width,
                material.Height,
                material.MipCount,
                bc1Bytes = material.Bc1Data.Length,
            },
            pages = pages.Count,
            vertexCount,
            triangleCount,
            geometryBytes,
        };
        File.WriteAllText(
            Path.Combine(temporaryRoot, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        Directory.Move(temporaryRoot, outputRoot);

        Console.WriteLine(
            $"[rock-bake] OK continuous RMP2: pages={pages.Count}, vertices={vertexCount:N0}, "
            + $"triangles={triangleCount:N0}, geometry={geometryBytes / (1024.0 * 1024.0):F1} MiB, "
            + $"material={material.Bc1Data.Length / (1024.0 * 1024.0):F2} MiB, "
            + $"time={stopwatch.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"[rock-bake] output: {outputRoot}");
        return 0;
    }
    catch
    {
        if (Directory.Exists(temporaryRoot))
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }

        throw;
    }
}

static RockAlbedoTile DecodeAlbedo(PhotogrammetryRockPrimitive primitive)
{
    byte[] encoded = primitive.BaseColorImageBytes
        ?? throw new InvalidDataException("Photogrammetry scan has no base-colour texture.");
    using SKBitmap bitmap = SKBitmap.Decode(encoded)
        ?? throw new InvalidDataException("Cannot decode a scan base-colour texture.");
    var rgba = new byte[checked(bitmap.Width * bitmap.Height * 4)];
    for (int y = 0; y < bitmap.Height; y++)
    {
        for (int x = 0; x < bitmap.Width; x++)
        {
            SKColor pixel = bitmap.GetPixel(x, y);
            int offset = ((y * bitmap.Width) + x) * 4;
            rgba[offset] = pixel.Red;
            rgba[offset + 1] = pixel.Green;
            rgba[offset + 2] = pixel.Blue;
            rgba[offset + 3] = pixel.Alpha;
        }
    }

    Console.WriteLine($"[rock-bake] albedo {bitmap.Width}x{bitmap.Height}");
    return new RockAlbedoTile(bitmap.Width, bitmap.Height, rgba);
}

static void PrintUsage()
{
    Console.WriteLine(
        """
        MapaTur.RockBake — offline scanned-rock microgeometry bake

        Required:
          --dem <z17 .bdt>
          --base-dem <tatry.dem>
          --height <scan1.jpg;scan2.jpg;scan3.jpg>
          --out <new output directory>

        Optional:
          --feature <metres>     scanned feature size (default 4.0)
          --amplitude <metres>   maximum physical relief (default 0.55)
          --page <metres>        streaming page size (default 16)
          --lod <list>           comma-separated RMP LODs (default 0,1,2)

        Continuous DEM-conforming RMP2:
          --continuous-rmp2
          --scan <scan1.gltf;scan2.gltf;scan3.gltf>
          --relief-scan <scan1.gltf;scan2.gltf>  extract relief from real mesh fronts
          --maximum-relief <m>   hard outward relief bound (default 2.0)
          --edge <m>             maximum refined mesh edge (default 0.65)
          --seed <n>             geometry/material synthesis seed
          --material-resolution <px>  unique square material size (default 2048)
          --material-page <id>   RTX1 material page id (default 20)
          --neutralize-albedo    remove warm colour cast (off by default)
          --flat-albedo          diagnostic neutral slate; shape comes only from 3D relief
        """);
}
