using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.Json;

using MapaTur.Application.Terrain;

using SkiaSharp;

string? scanPath = GetArgument("--scan");
string? outputRoot = GetArgument("--out");
string? centerText = GetArgument("--center");
string? normalText = GetArgument("--normal");
if (scanPath is null || outputRoot is null || centerText is null || normalText is null)
{
    PrintUsage();
    return 2;
}

EnsureFile(scanPath);
if (Directory.Exists(outputRoot))
{
    throw new IOException($"Output already exists: {outputRoot}");
}

Vector3 center = ParseVector(centerText, "--center");
Vector3 normal = ParseVector(normalText, "--normal");
float heightMeters = ParsePositive(GetArgument("--height") ?? "18", "--height");
float pageMeters = ParsePositive(GetArgument("--page") ?? "16", "--page");
string? wallRmp1Root = GetArgument("--wall-rmp1");
float edgeBlendFraction = ParsePositive(GetArgument("--edge-blend") ?? "0.15", "--edge-blend");
if (edgeBlendFraction > 0.5f)
{
    throw new ArgumentOutOfRangeException("--edge-blend", "Edge blend must not exceed 0.5.");
}

ushort materialPageId = ushort.Parse(GetArgument("--material") ?? "1", CultureInfo.InvariantCulture);
var stopwatch = Stopwatch.StartNew();

Console.WriteLine($"[rock-scan-bake] scan: {scanPath}");
Console.WriteLine(
    $"[rock-scan-bake] center={center}, normal={Vector3.Normalize(normal)}, "
    + $"height={heightMeters:F2} m, page={pageMeters:F2} m");

PhotogrammetryRockAsset asset = PhotogrammetryRockAsset.Load(scanPath);
if (asset.Primitives.Count != 1)
{
    throw new InvalidDataException(
        $"Pilot expects one scan primitive, but the asset contains {asset.Primitives.Count}.");
}

PhotogrammetryRockPrimitive source = asset.Primitives[0];
PhotogrammetryRockPrimitive fitted = RockScanPatchFitter.Fit(
    source,
    new RockScanPatchPlacement(center, normal, heightMeters));
if (wallRmp1Root is not null)
{
    if (!Directory.Exists(wallRmp1Root))
    {
        throw new DirectoryNotFoundException($"RMP1 wall source does not exist: {wallRmp1Root}");
    }

    Vector3 fittedMinimum = fitted.Positions.Aggregate(Vector3.Min);
    Vector3 fittedMaximum = fitted.Positions.Aggregate(Vector3.Max);
    var wallPoints = new List<Vector3>();
    foreach (string path in Directory.EnumerateFiles(
        wallRmp1Root,
        "*" + RockMeshPageStore.FileExtension,
        SearchOption.AllDirectories))
    {
        if (!path.Contains(
            $"{Path.DirectorySeparatorChar}lod0{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        using FileStream stream = File.OpenRead(path);
        RockMeshPage page = RockMeshPageStore.Read(stream);
        Vector3 pageMaximum = page.WorldMin + page.WorldExtent;
        if (pageMaximum.X < fittedMinimum.X - 3f
            || page.WorldMin.X > fittedMaximum.X + 3f
            || pageMaximum.Z < fittedMinimum.Z - 3f
            || page.WorldMin.Z > fittedMaximum.Z + 3f)
        {
            continue;
        }

        wallPoints.AddRange(UnpackPositions(page));
    }

    Vector3 outward = Vector3.Normalize(normal);
    float referenceDepth = Vector3.Dot(center, outward);
    wallPoints = wallPoints
        .Where(point => MathF.Abs(Vector3.Dot(point, outward) - referenceDepth) <= 30f)
        .ToList();
    if (wallPoints.Count == 0)
    {
        throw new InvalidDataException("No RMP1 wall points overlap the fitted scan.");
    }

    var wall = new RockWallSurfaceSampler(wallPoints, normal, cellSizeMeters: 0.5f);
    fitted = RockWallSurfaceConformer.Conform(
        fitted,
        new RockScanPatchPlacement(center, normal, heightMeters),
        wall,
        edgeBlendFraction);
    Console.WriteLine(
        $"[rock-scan-bake] conformed+welded to {wallPoints.Count:N0} wall samples, "
        + $"edge blend={edgeBlendFraction:P0}");
}

IReadOnlyList<ScannedRockMeshPage> pages = ScannedRockPageBaker.Bake(
    fitted,
    pageMeters,
    lod: 0,
    geometricError: 0f,
    materialPageId);
RockMaterialPage material = BakeMaterial(source, materialPageId);

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
        RockMaterialPage verified = RockMaterialPageStore.Read(stream);
        if (verified.Bc1Data.Length != material.Bc1Data.Length)
        {
            throw new InvalidDataException("RTX1 verification failed.");
        }
    }

    var manifest = new
    {
        format = "RMP2+RTX1",
        source = Path.GetFileName(scanPath),
        license = "CC0-1.0",
        center = new[] { center.X, center.Y, center.Z },
        outwardNormal = new[] { normal.X, normal.Y, normal.Z },
        heightMeters,
        pageMeters,
        wallRmp1 = wallRmp1Root is null ? null : Path.GetFileName(Path.TrimEndingDirectorySeparator(wallRmp1Root)),
        edgeBlendFraction = wallRmp1Root is null ? (float?)null : edgeBlendFraction,
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
        $"[rock-scan-bake] OK: pages={pages.Count}, vertices={vertexCount:N0}, triangles={triangleCount:N0}, "
        + $"geometry={geometryBytes / (1024.0 * 1024.0):F1} MiB, "
        + $"material={material.Bc1Data.Length / (1024.0 * 1024.0):F2} MiB, "
        + $"time={stopwatch.Elapsed.TotalSeconds:F1}s");
    Console.WriteLine($"[rock-scan-bake] output: {outputRoot}");
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

static RockMaterialPage BakeMaterial(PhotogrammetryRockPrimitive source, ushort pageId)
{
    byte[] encoded = source.BaseColorImageBytes
        ?? throw new InvalidDataException("Photogrammetric scan has no base-colour texture.");
    using SKBitmap bitmap = SKBitmap.Decode(encoded)
        ?? throw new InvalidDataException("Cannot decode the scan base-colour texture.");
    var rgba = new byte[checked(bitmap.Width * bitmap.Height * 4)];
    for (int y = 0; y < bitmap.Height; y++)
    {
        for (int x = 0; x < bitmap.Width; x++)
        {
            SKColor pixel = bitmap.GetPixel(x, y);
            float luminance = ((0.2126f * pixel.Red) + (0.7152f * pixel.Green) + (0.0722f * pixel.Blue));
            int offset = ((y * bitmap.Width) + x) * 4;
            rgba[offset] = Mix(pixel.Red, luminance * 0.94f);
            rgba[offset + 1] = Mix(pixel.Green, luminance * 0.98f);
            rgba[offset + 2] = Mix(pixel.Blue, luminance);
            rgba[offset + 3] = pixel.Alpha;
        }
    }

    Console.WriteLine($"[rock-scan-bake] albedo: {bitmap.Width}x{bitmap.Height} -> neutral BC1+mips");
    return RockMaterialPageBaker.Bake(pageId, rgba, bitmap.Width, bitmap.Height);
}

static Vector3[] UnpackPositions(RockMeshPage page)
{
    var positions = new Vector3[page.VertexCount];
    for (int i = 0; i < page.VertexCount; i++)
    {
        ReadOnlySpan<byte> vertex = page.VertexData.AsSpan(
            i * RockMeshPage.VertexStrideBytes,
            RockMeshPage.VertexStrideBytes);
        positions[i] = new Vector3(
            page.WorldMin.X
                + ((BinaryPrimitives.ReadUInt16LittleEndian(vertex) / (float)ushort.MaxValue) * page.WorldExtent.X),
            page.WorldMin.Y
                + ((BinaryPrimitives.ReadUInt16LittleEndian(vertex[2..]) / (float)ushort.MaxValue) * page.WorldExtent.Y),
            page.WorldMin.Z
                + ((BinaryPrimitives.ReadUInt16LittleEndian(vertex[4..]) / (float)ushort.MaxValue) * page.WorldExtent.Z));
    }

    return positions;
}

static byte Mix(byte source, float neutral)
{
    float value = (source * 0.32f) + (neutral * 0.68f);
    return (byte)Math.Clamp(MathF.Round(value), 0f, 255f);
}

static Vector3 ParseVector(string text, string name)
{
    string[] parts = text.Split(';', StringSplitOptions.TrimEntries);
    if (parts.Length != 3)
    {
        throw new ArgumentException($"{name} must contain x;y;z.");
    }

    return new Vector3(
        float.Parse(parts[0], CultureInfo.InvariantCulture),
        float.Parse(parts[1], CultureInfo.InvariantCulture),
        float.Parse(parts[2], CultureInfo.InvariantCulture));
}

static float ParsePositive(string value, string name)
{
    if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
        || !float.IsFinite(parsed)
        || parsed <= 0f)
    {
        throw new ArgumentException($"{name} must be finite and positive.");
    }

    return parsed;
}

static void EnsureFile(string path)
{
    if (!File.Exists(path))
    {
        throw new FileNotFoundException("Photogrammetric input does not exist.", path);
    }
}

static void PrintUsage()
{
    Console.WriteLine(
        """
        MapaTur.RockScanBake — offline photogrammetric cliff bake

        Required:
          --scan <model.gltf|model.glb>
          --out <new output directory>
          --center <worldX;worldY;worldZ>
          --normal <outwardX;outwardY;outwardZ>

        Optional:
          --height <metres>      fitted scan height (default 18)
          --page <metres>        streaming page size (default 16)
          --material <id>        material-page id (default 1)
          --wall-rmp1 <root>     pilot DEM-wall samples used for conforming/welding
          --edge-blend <0..0.5>  welded boundary fraction (default 0.15)
        """);
}
