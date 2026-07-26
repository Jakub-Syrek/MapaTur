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
        """);
}
