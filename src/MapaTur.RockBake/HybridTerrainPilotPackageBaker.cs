using System.Numerics;
using System.Text.Json;

using MapaTur.Application.Terrain;

namespace MapaTur.RockBake;

public sealed record HybridTerrainPilotBakeOptions(
    float PageSizeMeters,
    float MaximumEdgeMeters,
    float SampleAmplitudeMeters,
    float MaximumReliefMeters,
    int Seed,
    float Lod1TargetTriangleFraction = 0.35f,
    float Lod1MaximumErrorMeters = 0.35f,
    float Lod2TargetTriangleFraction = 0.12f,
    float Lod2MaximumErrorMeters = 1.2f);

public readonly record struct HybridTerrainPilotBakeResult(
    int PageCount,
    long VertexCount,
    long TriangleCount,
    long FinalPayloadBytes,
    long PeakTemporaryBytes,
    double CoveredAreaSquareMeters);

/// <summary>
/// Produces an atomic geometry-only RMP3 pilot. It deliberately has no renderer or AppData dependency:
/// ortho integration remains owned by the post-det05 runtime merge.
/// </summary>
public static class HybridTerrainPilotPackageBaker
{
    private const byte MinimumVisibleRockWeight = 96;
    private const float MinimumVisibleRockFraction = 0.02f;

    public static HybridTerrainPilotBakeResult Bake(
        IReadOnlyList<RockMeshTriangle> source,
        Func<Vector3, Vector3, RockSurfaceSample> sampleSurface,
        Func<Vector3, Vector2> orthoUvForPosition,
        string outputRoot,
        HybridTerrainPilotBakeOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sampleSurface);
        ArgumentNullException.ThrowIfNull(orthoUvForPosition);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        ArgumentNullException.ThrowIfNull(options);
        ValidatePositive(options.PageSizeMeters, nameof(options.PageSizeMeters));
        ValidatePositive(options.MaximumEdgeMeters, nameof(options.MaximumEdgeMeters));
        ValidatePositive(options.SampleAmplitudeMeters, nameof(options.SampleAmplitudeMeters));
        ValidatePositive(options.MaximumReliefMeters, nameof(options.MaximumReliefMeters));
        ValidateFraction(options.Lod1TargetTriangleFraction, nameof(options.Lod1TargetTriangleFraction));
        ValidatePositive(options.Lod1MaximumErrorMeters, nameof(options.Lod1MaximumErrorMeters));
        ValidateFraction(options.Lod2TargetTriangleFraction, nameof(options.Lod2TargetTriangleFraction));
        ValidatePositive(options.Lod2MaximumErrorMeters, nameof(options.Lod2MaximumErrorMeters));
        if (Directory.Exists(outputRoot) || File.Exists(outputRoot))
        {
            throw new IOException($"RMP3 pilot output already exists: {outputRoot}");
        }

        HybridTerrainMesh surface = ContinuousScannedRockSurfaceBuilder.BuildHybrid(
            source,
            sampleSurface,
            options.SampleAmplitudeMeters,
            options.MaximumReliefMeters,
            options.MaximumEdgeMeters,
            options.Seed,
            orthoUvForPosition);
        var simplifier = new MeshoptimizerScannedRockIndexSimplifier();
        HybridTerrainMeshPage[] lod0 = HybridTerrainPageBaker.Bake(
            surface,
            options.PageSizeMeters,
            lod: 0,
            geometricError: 0f)
            .Where(HasVisibleRock)
            .ToArray();
        HybridTerrainMeshPage[] lod1 = HybridTerrainPageBaker.Bake(
                surface,
                options.PageSizeMeters * 2f,
                lod: 0,
                geometricError: 0f)
            .Where(HasVisibleRock)
            .Select(page => HybridTerrainPageLodBuilder.Build(
                page,
                lod: 1,
                options.Lod1TargetTriangleFraction,
                options.Lod1MaximumErrorMeters,
                simplifier))
            .Where(HasVisibleRock)
            .ToArray();
        HybridTerrainMeshPage[] lod2 = HybridTerrainPageBaker.Bake(
                surface,
                options.PageSizeMeters * 4f,
                lod: 0,
                geometricError: 0f)
            .Where(HasVisibleRock)
            .Select(page => HybridTerrainPageLodBuilder.Build(
                page,
                lod: 2,
                options.Lod2TargetTriangleFraction,
                options.Lod2MaximumErrorMeters,
                simplifier))
            .Where(HasVisibleRock)
            .ToArray();
        HybridTerrainMeshPage[] pages = [.. lod0, .. lod1, .. lod2];
        if (pages.Length == 0)
        {
            throw new InvalidOperationException("The RMP3 pilot contains no page with a visible rock contribution.");
        }

        string temporaryRoot = outputRoot + $".tmp-{Guid.NewGuid():N}";
        try
        {
            Directory.CreateDirectory(temporaryRoot);
            long vertexCount = 0;
            long triangleCount = 0;
            var descriptors = new List<HybridTerrainPageDescriptor>(pages.Length);
            foreach (HybridTerrainMeshPage page in pages)
            {
                string relative = HybridTerrainMeshPageStore.RelativePathFor(page.Lod, page.PageX, page.PageY);
                string path = Path.Combine(temporaryRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using (FileStream stream = File.Create(path))
                {
                    HybridTerrainMeshPageStore.Write(stream, page);
                }

                using (FileStream stream = File.OpenRead(path))
                {
                    HybridTerrainMeshPage verified = HybridTerrainMeshPageStore.Read(stream);
                    if (verified.VertexCount != page.VertexCount || verified.IndexCount != page.IndexCount)
                    {
                        throw new InvalidDataException($"RMP3 pilot verification failed for {relative}.");
                    }
                }

                descriptors.Add(new HybridTerrainPageDescriptor(
                    new HybridTerrainPageKey(page.PageX, page.PageY, page.Lod),
                    page.WorldMin,
                    page.WorldExtent,
                    page.GeometricError,
                    page.VertexCount,
                    page.IndexCount,
                    path));
                vertexCount += page.VertexCount;
                triangleCount += page.IndexCount / 3;
            }

            HybridTerrainPageHierarchyValidator.Validate(descriptors);
            HybridTerrainPageIndexStore.Write(temporaryRoot, descriptors);
            double coveredArea = lod0.Length * options.PageSizeMeters * options.PageSizeMeters;
            var manifest = new
            {
                format = "RMP3-hybrid-terrain-pilot",
                options.PageSizeMeters,
                options.MaximumEdgeMeters,
                options.SampleAmplitudeMeters,
                options.MaximumReliefMeters,
                options.Seed,
                pages = new
                {
                    lod0 = lod0.Length,
                    lod1 = lod1.Length,
                    lod2 = lod2.Length,
                    total = pages.Length,
                },
                vertexCount,
                triangleCount,
                coveredAreaSquareMeters = coveredArea,
                maskTextureBytes = 0,
                additionalSamplerCount = 0,
            };
            File.WriteAllText(
                Path.Combine(temporaryRoot, "manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            long finalBytes = Directory
                .EnumerateFiles(temporaryRoot, "*", SearchOption.AllDirectories)
                .Sum(path => new FileInfo(path).Length);
            Directory.Move(temporaryRoot, outputRoot);
            return new HybridTerrainPilotBakeResult(
                pages.Length,
                vertexCount,
                triangleCount,
                finalBytes,
                PeakTemporaryBytes: 0,
                coveredArea);
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

    private static bool HasVisibleRock(HybridTerrainMeshPage page)
    {
        int visible = 0;
        for (int vertex = 0; vertex < page.VertexCount; vertex++)
        {
            int offset = (vertex * HybridTerrainMeshPage.VertexStrideBytes) + 15;
            if (page.VertexData[offset] >= MinimumVisibleRockWeight)
            {
                visible++;
            }
        }

        return visible > 0 && visible / (float)page.VertexCount >= MinimumVisibleRockFraction;
    }

    private static void ValidatePositive(float value, string parameterName)
    {
        if (!float.IsFinite(value) || value <= 0f)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void ValidateFraction(float value, string parameterName)
    {
        if (!float.IsFinite(value) || value <= 0f || value >= 1f)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
