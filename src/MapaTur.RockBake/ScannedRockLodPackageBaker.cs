using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

using MapaTur.Application.Terrain;

namespace MapaTur.RockBake;

public readonly record struct ScannedRockPageRange(
    int MinimumX,
    int MinimumY,
    int MaximumX,
    int MaximumY)
{
    public bool Contains(int pageX, int pageY) =>
        pageX >= MinimumX
        && pageX <= MaximumX
        && pageY >= MinimumY
        && pageY <= MaximumY;
}

public sealed record ScannedRockLodPackageOptions(
    float Lod1TriangleFraction = 0.35f,
    float Lod1MaximumErrorMeters = 0.35f,
    float Lod2TriangleFraction = 0.12f,
    float Lod2MaximumErrorMeters = 1.2f,
    int MaximumDegreeOfParallelism = 0,
    ScannedRockPageRange? PageRange = null);

public readonly record struct ScannedRockLodPackageResult(
    int SourcePageCount,
    int OutputPageCount,
    long SourceVertexCount,
    long SourceTriangleCount,
    long OutputVertexCount,
    long OutputTriangleCount,
    long OutputGeometryBytes,
    TimeSpan Elapsed);

/// <summary>
/// Converts an accepted LOD0-only RMP2 package into an atomic same-cell LOD0/1/2 package. The source
/// directory is read-only; output is published only after all pages and the spatial index validate.
/// </summary>
public static class ScannedRockLodPackageBaker
{
    public static ScannedRockLodPackageResult Bake(
        string sourceRoot,
        string outputRoot,
        ScannedRockLodPackageOptions options,
        IScannedRockIndexSimplifier simplifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(simplifier);
        sourceRoot = Path.GetFullPath(sourceRoot);
        outputRoot = Path.GetFullPath(outputRoot);
        ValidateOptions(sourceRoot, outputRoot, options);

        ScannedRockPageDescriptor[] sourcePages = LoadSourcePages(sourceRoot)
            .Where(page => page.Key.Lod == 0)
            .Where(page => options.PageRange?.Contains(page.Key.PageX, page.Key.PageY) ?? true)
            .OrderBy(page => page.Key.PageX)
            .ThenBy(page => page.Key.PageY)
            .ToArray();
        if (sourcePages.Length == 0)
        {
            throw new InvalidDataException("The requested RMP2 package/range has no LOD0 pages.");
        }

        string temporaryRoot = outputRoot + $".tmp-{Guid.NewGuid():N}";
        var outputDescriptors = new ConcurrentBag<ScannedRockPageDescriptor>();
        long sourceVertices = 0;
        long sourceTriangles = 0;
        long outputVertices = 0;
        long outputTriangles = 0;
        long outputBytes = 0;
        int completed = 0;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            Directory.CreateDirectory(temporaryRoot);
            CopyPackageAssets(sourceRoot, temporaryRoot);
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = options.MaximumDegreeOfParallelism > 0
                    ? options.MaximumDegreeOfParallelism
                    : Math.Max(1, Environment.ProcessorCount / 2),
            };
            Parallel.ForEach(sourcePages, parallelOptions, descriptor =>
            {
                using FileStream sourceStream = File.OpenRead(descriptor.Path);
                ScannedRockMeshPage source = ScannedRockMeshPageStore.Read(sourceStream);
                ScannedRockMeshPage finest = ScannedRockPageLodBuilder.CreateFinestCopy(source);
                ScannedRockMeshPage lod1 = ScannedRockPageLodBuilder.Build(
                    source,
                    lod: 1,
                    options.Lod1TriangleFraction,
                    options.Lod1MaximumErrorMeters,
                    simplifier);
                ScannedRockMeshPage lod2Candidate = ScannedRockPageLodBuilder.Build(
                    source,
                    lod: 2,
                    options.Lod2TriangleFraction,
                    options.Lod2MaximumErrorMeters,
                    simplifier);
                ScannedRockMeshPage lod2 =
                    EnforceMonotonicCoarserLevel(lod1, lod2Candidate);
                ScannedRockMeshPage[] levels =
                [
                    finest,
                    lod1,
                    lod2,
                ];

                foreach (ScannedRockMeshPage level in levels)
                {
                    string relative = ScannedRockMeshPageStore.RelativePathFor(
                        level.Lod,
                        level.PageX,
                        level.PageY);
                    string path = Path.Combine(temporaryRoot, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    using (FileStream outputStream = File.Create(path))
                    {
                        ScannedRockMeshPageStore.Write(outputStream, level);
                    }

                    using FileStream verificationStream = File.OpenRead(path);
                    ScannedRockMeshPageHeader verified =
                        ScannedRockMeshPageStore.ReadHeader(verificationStream);
                    outputDescriptors.Add(ToDescriptor(verified, path));
                    Interlocked.Add(ref outputVertices, level.VertexCount);
                    Interlocked.Add(ref outputTriangles, level.IndexCount / 3);
                    Interlocked.Add(
                        ref outputBytes,
                        level.VertexData.LongLength
                        + (level.Indices.LongLength * sizeof(ushort)));
                }

                Interlocked.Add(ref sourceVertices, source.VertexCount);
                Interlocked.Add(ref sourceTriangles, source.IndexCount / 3);
                int nowCompleted = Interlocked.Increment(ref completed);
                if (nowCompleted % 1_000 == 0 || nowCompleted == sourcePages.Length)
                {
                    Console.WriteLine(
                        $"[rock-lod] {nowCompleted:N0}/{sourcePages.Length:N0} LOD0 pages, "
                        + $"elapsed={stopwatch.Elapsed.TotalSeconds:F1}s");
                }
            });

            ScannedRockPageDescriptor[] orderedOutput = outputDescriptors
                .OrderBy(page => page.Key.PageX)
                .ThenBy(page => page.Key.PageY)
                .ThenBy(page => page.Key.Lod)
                .ToArray();
            ScannedRockPageIndexStore.Write(temporaryRoot, orderedOutput);
            WriteManifest(
                temporaryRoot,
                sourceRoot,
                options,
                sourcePages.Length,
                orderedOutput.Length,
                sourceVertices,
                sourceTriangles,
                outputVertices,
                outputTriangles,
                outputBytes,
                stopwatch.Elapsed);
            Directory.Move(temporaryRoot, outputRoot);
            return new ScannedRockLodPackageResult(
                sourcePages.Length,
                orderedOutput.Length,
                sourceVertices,
                sourceTriangles,
                outputVertices,
                outputTriangles,
                outputBytes,
                stopwatch.Elapsed);
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

    private static void ValidateOptions(
        string sourceRoot,
        string outputRoot,
        ScannedRockLodPackageOptions options)
    {
        if (!Directory.Exists(sourceRoot))
        {
            throw new DirectoryNotFoundException(sourceRoot);
        }

        if (Directory.Exists(outputRoot) || File.Exists(outputRoot))
        {
            throw new IOException($"Output already exists: {outputRoot}");
        }

        string sourcePrefix = sourceRoot.TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (outputRoot.Equals(sourceRoot, StringComparison.OrdinalIgnoreCase)
            || outputRoot.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("LOD output must not be placed inside its source package.", nameof(outputRoot));
        }

        if (!IsFraction(options.Lod1TriangleFraction)
            || !IsFraction(options.Lod2TriangleFraction)
            || !IsPositive(options.Lod1MaximumErrorMeters)
            || !IsPositive(options.Lod2MaximumErrorMeters)
            || options.Lod2TriangleFraction >= options.Lod1TriangleFraction
            || options.Lod2MaximumErrorMeters <= options.Lod1MaximumErrorMeters
            || options.MaximumDegreeOfParallelism < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (options.PageRange is { } range
            && (range.MinimumX > range.MaximumX || range.MinimumY > range.MaximumY))
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    private static IReadOnlyList<ScannedRockPageDescriptor> LoadSourcePages(string sourceRoot)
    {
        string indexPath = Path.Combine(sourceRoot, ScannedRockPageIndexStore.FileName);
        if (File.Exists(indexPath))
        {
            return ScannedRockPageIndexStore.Read(sourceRoot);
        }

        var result = new List<ScannedRockPageDescriptor>();
        foreach (string path in Directory.EnumerateFiles(
            sourceRoot,
            "*" + ScannedRockMeshPageStore.FileExtension,
            SearchOption.AllDirectories))
        {
            using FileStream stream = File.OpenRead(path);
            result.Add(ToDescriptor(ScannedRockMeshPageStore.ReadHeader(stream), path));
        }

        return result;
    }

    private static void CopyPackageAssets(string sourceRoot, string temporaryRoot)
    {
        foreach (string path in Directory.EnumerateFiles(
            sourceRoot,
            "*" + RockMaterialPageStore.FileExtension,
            SearchOption.TopDirectoryOnly))
        {
            File.Copy(path, Path.Combine(temporaryRoot, Path.GetFileName(path)));
        }

        string sourceManifest = Path.Combine(sourceRoot, "manifest.json");
        if (File.Exists(sourceManifest))
        {
            File.Copy(sourceManifest, Path.Combine(temporaryRoot, "_source-manifest.json"));
        }
    }

    private static ScannedRockMeshPage EnforceMonotonicCoarserLevel(
        ScannedRockMeshPage finer,
        ScannedRockMeshPage coarserCandidate)
    {
        bool candidateIsLighter = coarserCandidate.IndexCount <= finer.IndexCount;
        ScannedRockMeshPage geometry = candidateIsLighter ? coarserCandidate : finer;
        return new ScannedRockMeshPage(
            coarserCandidate.Lod,
            coarserCandidate.PageX,
            coarserCandidate.PageY,
            coarserCandidate.WorldMin,
            coarserCandidate.WorldExtent,
            MathF.Max(finer.GeometricError, coarserCandidate.GeometricError),
            coarserCandidate.MaterialPageId,
            geometry.VertexData,
            geometry.Indices);
    }

    private static void WriteManifest(
        string temporaryRoot,
        string sourceRoot,
        ScannedRockLodPackageOptions options,
        int sourcePageCount,
        int outputPageCount,
        long sourceVertices,
        long sourceTriangles,
        long outputVertices,
        long outputTriangles,
        long outputBytes,
        TimeSpan elapsed)
    {
        var manifest = new
        {
            format = "RMP2-same-cell-LOD",
            sourceRoot,
            sourcePageCount,
            outputPageCount,
            sourceVertices,
            sourceTriangles,
            outputVertices,
            outputTriangles,
            outputBytes,
            elapsedSeconds = elapsed.TotalSeconds,
            lod0 = new { exactV91Payload = true },
            lod1 = new
            {
                triangleFraction = options.Lod1TriangleFraction,
                maximumErrorMeters = options.Lod1MaximumErrorMeters,
            },
            lod2 = new
            {
                triangleFraction = options.Lod2TriangleFraction,
                maximumErrorMeters = options.Lod2MaximumErrorMeters,
            },
            borderLocked = true,
            absoluteError = true,
            pageRange = options.PageRange,
            simplifier = "meshoptimizer",
        };
        File.WriteAllText(
            Path.Combine(temporaryRoot, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static ScannedRockPageDescriptor ToDescriptor(
        ScannedRockMeshPageHeader header,
        string path) =>
        new(
            new ScannedRockPageKey(header.PageX, header.PageY, header.Lod),
            header.WorldMin,
            header.WorldExtent,
            header.GeometricError,
            header.MaterialPageId,
            header.VertexCount,
            header.IndexCount,
            path);

    private static bool IsFraction(float value) =>
        float.IsFinite(value) && value > 0f && value < 1f;

    private static bool IsPositive(float value) =>
        float.IsFinite(value) && value > 0f;
}
