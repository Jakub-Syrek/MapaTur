using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.Json;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

using SkiaSharp;

string[] scanPaths = GetArguments("--scan");
string? outputRoot = GetArgument("--out");
string? centerText = GetArgument("--center");
string? normalText = GetArgument("--normal");
if (scanPaths.Length == 0 || outputRoot is null || centerText is null || normalText is null)
{
    PrintUsage();
    return 2;
}

foreach (string path in scanPaths)
{
    EnsureFile(path);
}

if (Directory.Exists(outputRoot))
{
    throw new IOException($"Output already exists: {outputRoot}");
}

Vector3 center = ParseVector(centerText, "--center");
Vector3 normal = ParseVector(normalText, "--normal");
float heightMeters = ParsePositive(GetArgument("--height") ?? "18", "--height");
float depthMeters = GetArgument("--depth") is { } depthText
    ? ParsePositive(depthText, "--depth")
    : heightMeters * 0.25f;
float? coverageWidthMeters = GetArgument("--cover-width") is { } coverageWidthText
    ? ParsePositive(coverageWidthText, "--cover-width")
    : null;
float? coverageHeightMeters = GetArgument("--cover-height") is { } coverageHeightText
    ? ParsePositive(coverageHeightText, "--cover-height")
    : null;
float coverageOverlap = ParsePositive(GetArgument("--cover-overlap") ?? "0.28", "--cover-overlap");
int coverageSeed = int.Parse(GetArgument("--seed") ?? "271828", CultureInfo.InvariantCulture);
bool mirrorVariants = HasFlag("--mirror-variants");
bool internalWarp = HasFlag("--internal-warp");
bool autoSteep = HasFlag("--auto-steep");
bool analyzeSteepOnly = HasFlag("--analyze-steep");
float steepSlopeDegrees = ParsePositive(GetArgument("--steep-slope") ?? "58", "--steep-slope");
float steepBlockMeters = ParsePositive(GetArgument("--steep-block") ?? "28", "--steep-block");
float steepCoverageFraction = ParsePositive(
    GetArgument("--steep-coverage") ?? "0.20",
    "--steep-coverage");
int maxInstances = int.Parse(GetArgument("--max-instances") ?? "240", CultureInfo.InvariantCulture);
if (maxInstances <= 0)
{
    throw new ArgumentOutOfRangeException("--max-instances");
}
bool coverageMode =
    autoSteep || coverageWidthMeters is not null || coverageHeightMeters is not null || scanPaths.Length > 1;
if (coverageMode
    && ((mirrorVariants ? scanPaths.Length < 2 : scanPaths.Length < 3)
        || (!autoSteep && (coverageWidthMeters is null || coverageHeightMeters is null))))
{
    throw new ArgumentException(
        "Coverage requires at least three --scan variants "
        + "(or two with --mirror-variants) "
        + "and either --auto-steep or explicit dimensions.");
}

float pageMeters = ParsePositive(GetArgument("--page") ?? "16", "--page");
float meshClusterCellMeters = ParseNonNegative(
    GetArgument("--mesh-cell") ?? "0",
    "--mesh-cell");
string? wallRmp1Root = GetArgument("--wall-rmp1");
string? wallDemPath = GetArgument("--wall-dem");
string? wallAnchorText = GetArgument("--anchor");
float edgeBlendFraction = ParsePositive(GetArgument("--edge-blend") ?? "0.15", "--edge-blend");
float interiorClearanceMeters = ParseNonNegative(
    GetArgument("--clearance") ?? "0.35",
    "--clearance");
float maximumReliefMeters = ParsePositive(
    GetArgument("--max-relief") ?? "3",
    "--max-relief");
if (edgeBlendFraction > 0.5f)
{
    throw new ArgumentOutOfRangeException("--edge-blend", "Edge blend must not exceed 0.5.");
}

if (wallRmp1Root is not null && wallDemPath is not null)
{
    throw new ArgumentException("Use either --wall-rmp1 or --wall-dem, not both.");
}

if ((wallDemPath is null) != (wallAnchorText is null))
{
    throw new ArgumentException("--wall-dem and --anchor must be specified together.");
}

if (autoSteep && wallDemPath is null)
{
    throw new ArgumentException("--auto-steep requires --wall-dem and --anchor.");
}

ushort materialPageId = ushort.Parse(GetArgument("--material") ?? "1", CultureInfo.InvariantCulture);
int? autoRegionCount = null;
int? coveragePatchCount = null;
var stopwatch = Stopwatch.StartNew();

Console.WriteLine(
    $"[rock-scan-bake] center={center}, normal={Vector3.Normalize(normal)}, "
    + $"height={heightMeters:F2} m, depth={depthMeters:F2} m, page={pageMeters:F2} m");

var sources = new List<PhotogrammetryRockPrimitive>(scanPaths.Length);
foreach (string scanPath in scanPaths)
{
    Console.WriteLine($"[rock-scan-bake] scan: {scanPath}");
    PhotogrammetryRockAsset asset = PhotogrammetryRockAsset.Load(scanPath);
    if (asset.Primitives.Count != 1)
    {
        throw new InvalidDataException(
            $"Each scan variant must contain one primitive, but {scanPath} contains {asset.Primitives.Count}.");
    }

    sources.Add(asset.Primitives[0]);
}

if (mirrorVariants)
{
    var oriented = new List<PhotogrammetryRockPrimitive>(sources.Count * 2);
    foreach (PhotogrammetryRockPrimitive original in sources)
    {
        oriented.Add(original);
        oriented.Add(PhotogrammetryRockVariantTransformer.MirrorHorizontal(original));
    }

    sources = oriented;
    Console.WriteLine(
        $"[rock-scan-bake] expanded {scanPaths.Length} complete captures into {sources.Count} "
        + "full-detail orientation variants without cropping");
}

PhotogrammetryRockPrimitive source = sources[0];
if (internalWarp && !coverageMode)
{
    source = PhotogrammetryRockInternalWarper.Warp(source, coverageSeed);
    Console.WriteLine(
        $"[rock-scan-bake] applied deterministic full-scan interior warp seed={coverageSeed}; "
        + "outer frame and measured topology preserved");
}

var placement = new RockScanPatchPlacement(center, normal, heightMeters, depthMeters);
PhotogrammetryRockPrimitive? fitted = coverageMode
    ? null
    : RockScanPatchFitter.Fit(source, placement);
Dictionary<ScannedRockPageKey, ScannedRockMeshPage>? prebakedAutoPages = null;
byte[]? prebakedMaterialImageBytes = null;
if (wallRmp1Root is not null || wallDemPath is not null)
{
    List<Vector3> wallPoints;
    string wallSource;
    DemRaster? wallRaster = null;
    GeoPoint? wallProjectionAnchor = null;
    if (wallDemPath is not null)
    {
        EnsureFile(wallDemPath);
        GeoPoint anchor = ParseAnchor(wallAnchorText!);
        wallProjectionAnchor = anchor;
        using FileStream stream = File.OpenRead(wallDemPath);
        BakedDemTile tile = BakedDemTileStore.Read(stream);
        wallRaster = BakedTileMeshBuilder.AsRaster(tile);
        TerrainMesh3D mesh = TerrainMesh3D.Build(
            wallRaster,
            new TerrainMeshOptions
            {
                VerticalExaggeration = 1f,
                SkirtDepthMeters = 0f,
                NormalApronCells = 0,
            },
            anchor);
        wallPoints = mesh.Vertices.ToList();
        wallSource = $"raw DEM z{tile.Zoom}/{tile.TileX}/{tile.TileY}";
    }
    else
    {
        if (!Directory.Exists(wallRmp1Root))
        {
            throw new DirectoryNotFoundException($"RMP1 wall source does not exist: {wallRmp1Root}");
        }

        float coverageRadius = coverageMode && !autoSteep
            ? MathF.Max(coverageWidthMeters!.Value, coverageHeightMeters!.Value)
            : 0f;
        Vector3 fittedMinimum = coverageMode && !autoSteep
            ? center - new Vector3(coverageRadius)
            : fitted!.Positions.Aggregate(Vector3.Min);
        Vector3 fittedMaximum = coverageMode && !autoSteep
            ? center + new Vector3(coverageRadius)
            : fitted!.Positions.Aggregate(Vector3.Max);
        wallPoints = [];
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

        wallSource = $"RMP1 {Path.GetFileName(Path.TrimEndingDirectorySeparator(wallRmp1Root))}";
    }

    if (!autoSteep)
    {
        Vector3 outward = Vector3.Normalize(normal);
        float referenceDepth = Vector3.Dot(center, outward);
        wallPoints = wallPoints
            .Where(point => MathF.Abs(Vector3.Dot(point, outward) - referenceDepth) <= 30f)
            .ToList();
    }

    if (wallPoints.Count == 0)
    {
        throw new InvalidDataException("No RMP1 wall points overlap the fitted scan.");
    }

    if (coverageMode)
    {
        float[] aspectRatios = sources
            .Select(primitive =>
            {
                float width = primitive.Positions.Max(point => point.X) - primitive.Positions.Min(point => point.X);
                float height = primitive.Positions.Max(point => point.Y) - primitive.Positions.Min(point => point.Y);
                return width / height;
            })
            .ToArray();
        byte[] atlasBytes = BuildAtlas(sources, out int atlasColumns, out int atlasRows);
        if (autoSteep)
        {
            var steepOptions = new SteepRockRegionOptions(
                steepSlopeDegrees,
                steepBlockMeters,
                steepCoverageFraction,
                MinimumWidthMeters: 10f,
                MinimumHeightMeters: 10f,
                BorderOverlapMeters: 4f);
            IReadOnlyList<SteepRockRegion> regions = SteepRockRegionPlanner.Plan(
                wallRaster!,
                wallProjectionAnchor!.Value,
                steepOptions);
            if (regions.Count == 0)
            {
                throw new InvalidDataException("No coherent steep DEM regions passed the auto-coverage gate.");
            }

            autoRegionCount = regions.Count;
            IReadOnlyList<IReadOnlyList<RockWallCoveragePatch>> regionPatchPlans = regions
                .Select((region, regionIndex) =>
                    RockWallCoveragePlanner.Plan(
                        new RockWallCoverageOptions(
                            region.Center,
                            region.OutwardNormal,
                            region.WidthMeters,
                            region.HeightMeters,
                            heightMeters,
                            depthMeters,
                            coverageOverlap,
                            unchecked(coverageSeed + (regionIndex * 104729))),
                        aspectRatios))
                .ToArray();
            int plannedPatchCount = regionPatchPlans.Sum(plan => plan.Count);
            Console.WriteLine(
                $"[rock-scan-bake] auto-steep analysis: regions={regions.Count}, "
                + $"slope>={steepSlopeDegrees:F1}°, block={steepBlockMeters:F1} m, "
                + $"coverage>={steepCoverageFraction:P0}, real 3D instances={plannedPatchCount}");
            for (int index = 0; index < regions.Count; index++)
            {
                SteepRockRegion region = regions[index];
                Console.WriteLine(
                    $"[rock-scan-bake] candidate {index + 1}: "
                    + $"{region.WidthMeters:F1}x{region.HeightMeters:F1} m, "
                    + $"normal={region.OutwardNormal}, samples={region.SteepSampleCount}, "
                    + $"instances={regionPatchPlans[index].Count}");
            }

            if (analyzeSteepOnly)
            {
                return 0;
            }

            if (plannedPatchCount > maxInstances)
            {
                throw new InvalidOperationException(
                    $"Auto-steep plan needs {plannedPatchCount} scan instances, above the "
                    + $"--max-instances safety budget of {maxInstances}. No geometry was generated.");
            }

            var pageAccumulator = new Dictionary<ScannedRockPageKey, ScannedRockMeshPage>();
            int totalPatches = 0;
            int bakedRegionCount = 0;
            for (int regionIndex = 0; regionIndex < regions.Count; regionIndex++)
            {
                SteepRockRegion region = regions[regionIndex];
                Vector3 regionOutward = Vector3.Normalize(region.OutwardNormal);
                Vector3 regionTangent = Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, regionOutward));
                float centerDepth = Vector3.Dot(region.Center, regionOutward);
                float centerTangent = Vector3.Dot(region.Center, regionTangent);
                float tangentRadius = (region.WidthMeters * 0.5f) + 8f;
                float elevationRadius = (region.HeightMeters * 0.5f) + 8f;
                List<Vector3> localWallPoints = wallPoints
                    .Where(point =>
                        MathF.Abs(Vector3.Dot(point, regionOutward) - centerDepth) <= steepBlockMeters + 12f
                        && MathF.Abs(Vector3.Dot(point, regionTangent) - centerTangent) <= tangentRadius
                        && MathF.Abs(point.Z - region.Center.Z) <= elevationRadius)
                    .ToList();
                if (localWallPoints.Count < 32)
                {
                    continue;
                }

                var localWall = new RockWallSurfaceSampler(
                    localWallPoints,
                    regionOutward,
                    cellSizeMeters: 0.5f);
                IReadOnlyList<RockWallCoveragePatch> patches = regionPatchPlans[regionIndex];
                totalPatches += patches.Count;
                PhotogrammetryRockPrimitive conformedRegion = RockWallCoverageComposer.Compose(
                    sources,
                    patches,
                    localWall,
                    edgeBlendFraction,
                    interiorClearanceMeters,
                    atlasColumns,
                    atlasRows,
                    atlasBaseColorImageBytes: null,
                    meshClusterCellMeters: meshClusterCellMeters,
                    internalWarpSeed: internalWarp ? unchecked(coverageSeed + (regionIndex * 104729)) : null,
                    maximumReliefMeters: maximumReliefMeters);
                IReadOnlyList<ScannedRockMeshPage> regionPages = ScannedRockPageBaker.Bake(
                    conformedRegion,
                    pageMeters,
                    lod: 0,
                    geometricError: 0f,
                    materialPageId);
                foreach (ScannedRockMeshPage page in regionPages)
                {
                    var key = new ScannedRockPageKey(page.PageX, page.PageY, page.Lod);
                    pageAccumulator[key] = pageAccumulator.TryGetValue(key, out ScannedRockMeshPage? existing)
                        ? ScannedRockMeshPageCombiner.Combine(existing, page)
                        : page;
                }

                bakedRegionCount++;
                Console.WriteLine(
                    $"[rock-scan-bake] steep region {regionIndex + 1}/{regions.Count}: "
                    + $"{region.WidthMeters:F1}x{region.HeightMeters:F1} m, "
                    + $"normal={regionOutward}, patches={patches.Count}, "
                    + $"streaming pages={regionPages.Count}, accumulated={pageAccumulator.Count}");
            }

            if (bakedRegionCount == 0)
            {
                throw new InvalidDataException("Steep regions had no usable local DEM wall samples.");
            }

            coveragePatchCount = totalPatches;
            prebakedAutoPages = pageAccumulator;
            prebakedMaterialImageBytes = atlasBytes;
            Console.WriteLine(
                $"[rock-scan-bake] auto-steep coverage: regions={bakedRegionCount}, "
                + $"real 3D scan instances={totalPatches}, variants={sources.Count}, "
                + $"incrementally packed pages={pageAccumulator.Count}");
        }
        else
        {
            var wall = new RockWallSurfaceSampler(wallPoints, normal, cellSizeMeters: 0.5f);
            var coverageOptions = new RockWallCoverageOptions(
                center,
                normal,
                coverageWidthMeters!.Value,
                coverageHeightMeters!.Value,
                heightMeters,
                depthMeters,
                coverageOverlap,
                coverageSeed);
            IReadOnlyList<RockWallCoveragePatch> patches = RockWallCoveragePlanner.Plan(
                coverageOptions,
                aspectRatios);
            coveragePatchCount = patches.Count;
            fitted = RockWallCoverageComposer.Compose(
                sources,
                patches,
                wall,
                edgeBlendFraction,
                interiorClearanceMeters,
                atlasColumns,
                atlasRows,
                atlasBytes,
                meshClusterCellMeters,
                internalWarpSeed: internalWarp ? coverageSeed : null,
                maximumReliefMeters: maximumReliefMeters);
            Console.WriteLine(
                $"[rock-scan-bake] coverage: {patches.Count} real 3D scan instances, "
                + $"{coverageWidthMeters:F1}x{coverageHeightMeters:F1} m, variants={sources.Count}, "
                + $"seed={coverageSeed}, overlap={coverageOverlap:P0}");
        }
    }
    else
    {
        var wall = new RockWallSurfaceSampler(wallPoints, normal, cellSizeMeters: 0.5f);
        fitted = RockWallSurfaceConformer.Conform(
            fitted!,
            placement,
            wall,
            edgeBlendFraction,
            interiorClearanceMeters,
            maximumReliefMeters: maximumReliefMeters);
    }

    Console.WriteLine(
        $"[rock-scan-bake] conformed+welded to {wallPoints.Count:N0} {wallSource} samples, "
        + $"edge blend={edgeBlendFraction:P0}, clearance={interiorClearanceMeters:F2} m");
    Console.WriteLine(
        $"[rock-scan-bake] relief bounded to <= {maximumReliefMeters:F2} m from local DEM wall");
}
else if (coverageMode)
{
    throw new ArgumentException("Coverage requires --wall-rmp1 or --wall-dem to preserve the terrain shape.");
}

IReadOnlyList<ScannedRockMeshPage> pages = prebakedAutoPages is not null
    ? prebakedAutoPages.Values
        .OrderBy(page => page.PageX)
        .ThenBy(page => page.PageY)
        .ThenBy(page => page.Lod)
        .ToArray()
    : ScannedRockPageBaker.Bake(
        fitted!,
        pageMeters,
        lod: 0,
        geometricError: 0f,
        materialPageId);
byte[] materialImageBytes = prebakedMaterialImageBytes
    ?? fitted?.BaseColorImageBytes
    ?? throw new InvalidDataException("Photogrammetric coverage has no base-colour texture.");
RockMaterialPage material = BakeMaterial(materialImageBytes, materialPageId);

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
        sources = scanPaths.Select(Path.GetFileName).ToArray(),
        license = "CC0-1.0",
        center = new[] { center.X, center.Y, center.Z },
        outwardNormal = new[] { normal.X, normal.Y, normal.Z },
        heightMeters,
        depthMeters,
        coverageWidthMeters,
        coverageHeightMeters,
        coverageOverlap = coverageMode ? coverageOverlap : (float?)null,
        coverageSeed = coverageMode ? coverageSeed : (int?)null,
        mirrorVariants,
        internalWarp,
        variantCount = sources.Count,
        autoSteep,
        autoRegionCount,
        coveragePatchCount,
        steepSlopeDegrees = autoSteep ? steepSlopeDegrees : (float?)null,
        steepBlockMeters = autoSteep ? steepBlockMeters : (float?)null,
        steepCoverageFraction = autoSteep ? steepCoverageFraction : (float?)null,
        pageMeters,
        meshClusterCellMeters,
        wallRmp1 = wallRmp1Root is null ? null : Path.GetFileName(Path.TrimEndingDirectorySeparator(wallRmp1Root)),
        wallDem = wallDemPath is null ? null : Path.GetFileName(wallDemPath),
        wallAnchor = wallAnchorText,
        edgeBlendFraction = wallRmp1Root is null && wallDemPath is null ? (float?)null : edgeBlendFraction,
        interiorClearanceMeters = wallRmp1Root is null && wallDemPath is null
            ? (float?)null
            : interiorClearanceMeters,
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

bool HasFlag(string name) => Array.IndexOf(args, name) >= 0;

string[] GetArguments(string name)
{
    var values = new List<string>();
    for (int i = 0; i + 1 < args.Length; i++)
    {
        if (args[i] == name)
        {
            values.Add(args[i + 1]);
            i++;
        }
    }

    return values.ToArray();
}

static byte[] BuildAtlas(
    IReadOnlyList<PhotogrammetryRockPrimitive> sources,
    out int columns,
    out int rows)
{
    var bitmaps = new List<SKBitmap>(sources.Count);
    try
    {
        foreach (PhotogrammetryRockPrimitive source in sources)
        {
            byte[] encoded = source.BaseColorImageBytes
                ?? throw new InvalidDataException("Photogrammetric scan has no base-colour texture.");
            bitmaps.Add(SKBitmap.Decode(encoded)
                ?? throw new InvalidDataException("Cannot decode a scan base-colour texture."));
        }

        byte[][] sourceRgba = bitmaps.Select(ToRgba).ToArray();
        IReadOnlyList<byte[]> harmonizedRgba = RockAlbedoHarmonizer.Harmonize(sourceRgba);
        for (int index = 0; index < bitmaps.Count; index++)
        {
            ApplyRgba(bitmaps[index], harmonizedRgba[index]);
        }

        columns = (int)Math.Ceiling(Math.Sqrt(bitmaps.Count));
        rows = (int)Math.Ceiling(bitmaps.Count / (double)columns);
        int cellWidth = bitmaps.Max(bitmap => bitmap.Width);
        int cellHeight = bitmaps.Max(bitmap => bitmap.Height);
        using var atlas = new SKBitmap(
            checked(cellWidth * columns),
            checked(cellHeight * rows),
            SKColorType.Rgba8888,
            SKAlphaType.Premul);
        using (var canvas = new SKCanvas(atlas))
        {
            canvas.Clear(new SKColor(105, 108, 105));
            using var paint = new SKPaint
            {
                IsAntialias = false,
            };
            for (int index = 0; index < bitmaps.Count; index++)
            {
                int column = index % columns;
                int row = index / columns;
                var destination = new SKRect(
                    column * cellWidth,
                    row * cellHeight,
                    (column + 1) * cellWidth,
                    (row + 1) * cellHeight);
                canvas.DrawBitmap(bitmaps[index], destination, paint);
            }
        }

        using SKImage image = SKImage.FromBitmap(atlas);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        Console.WriteLine(
            $"[rock-scan-bake] atlas: {columns}x{rows} cells, {atlas.Width}x{atlas.Height} px");
        return data.ToArray();
    }
    finally
    {
        foreach (SKBitmap bitmap in bitmaps)
        {
            bitmap.Dispose();
        }
    }
}

static byte[] ToRgba(SKBitmap bitmap)
{
    var rgba = new byte[checked(bitmap.Width * bitmap.Height * 4)];
    for (int y = 0; y < bitmap.Height; y++)
    {
        for (int x = 0; x < bitmap.Width; x++)
        {
            SKColor color = bitmap.GetPixel(x, y);
            int offset = ((y * bitmap.Width) + x) * 4;
            rgba[offset] = color.Red;
            rgba[offset + 1] = color.Green;
            rgba[offset + 2] = color.Blue;
            rgba[offset + 3] = color.Alpha;
        }
    }

    return rgba;
}

static void ApplyRgba(SKBitmap bitmap, byte[] rgba)
{
    for (int y = 0; y < bitmap.Height; y++)
    {
        for (int x = 0; x < bitmap.Width; x++)
        {
            int offset = ((y * bitmap.Width) + x) * 4;
            bitmap.SetPixel(
                x,
                y,
                new SKColor(rgba[offset], rgba[offset + 1], rgba[offset + 2], rgba[offset + 3]));
        }
    }
}

static RockMaterialPage BakeMaterial(byte[] encoded, ushort pageId)
{
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

static float ParseNonNegative(string value, string name)
{
    if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
        || !float.IsFinite(parsed)
        || parsed < 0f)
    {
        throw new ArgumentException($"{name} must be finite and non-negative.");
    }

    return parsed;
}

static GeoPoint ParseAnchor(string text)
{
    string[] parts = text.Split(';', StringSplitOptions.TrimEntries);
    if (parts.Length != 2)
    {
        throw new ArgumentException("--anchor must contain latitude;longitude.");
    }

    return new GeoPoint(
        double.Parse(parts[0], CultureInfo.InvariantCulture),
        double.Parse(parts[1], CultureInfo.InvariantCulture));
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
          --scan <model>         repeat at least 3 times for non-repeating wall coverage
          --mirror-variants      add a full-outline horizontal orientation of every scan
          --height <metres>      fitted scan/coverage-patch height (default 18)
          --depth <metres>       maximum outward relief (default 25% of height)
          --page <metres>        streaming page size (default 16)
          --mesh-cell <metres>   offline 3D vertex clustering cell; 0 keeps full scan
          --material <id>        material-page id (default 1)
          --cover-width <m>      width of multi-scan 3D wall shell
          --cover-height <m>     height of multi-scan 3D wall shell
          --cover-overlap <f>    overlap used to hide scan borders (default 0.28)
          --seed <integer>       deterministic coverage variation (default 271828)
          --internal-warp        vary the scan's interior 3D structure per seed/instance
          --auto-steep           detect coherent stretched-ortho DEM faces automatically
          --analyze-steep        list auto-detected facets without generating geometry
          --steep-slope <deg>    minimum auto-covered slope (default 58)
          --steep-block <m>      local wall-facet size (default 28)
          --steep-coverage <f>   steep-cell fraction required in a facet (default 0.20)
          --max-instances <n>    abort auto bake before geometry above this count (default 240)
          --wall-rmp1 <root>     pilot DEM-wall samples used for conforming/welding
          --wall-dem <tile.bdt>   exact raw runtime DEM used for conforming/welding
          --anchor <lat;lon>      shared world anchor required by --wall-dem
          --edge-blend <0..0.5>  welded boundary fraction (default 0.15)
          --clearance <metres>    interior-only anti-z-fighting offset (default 0.35)
          --max-relief <metres>   hard distance limit from local DEM wall (default 3)
        """);
}
