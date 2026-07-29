using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.RockBake;

namespace MapaTur.RockBake.Tests;

public sealed class ScannedRockLodPackageBakerTests : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), $"mapatur-rock-lod-bake-{Guid.NewGuid():N}");

    [Fact]
    public void should_create_complete_three_level_package_and_index()
    {
        // Arrange
        string sourceRoot = CreateSourcePackage();
        string outputRoot = Path.Combine(root, "output");
        var simplifier = new FirstTriangleSimplifier();
        var options = new ScannedRockLodPackageOptions(
            Lod1TriangleFraction: 0.5f,
            Lod1MaximumErrorMeters: 0.35f,
            Lod2TriangleFraction: 0.25f,
            Lod2MaximumErrorMeters: 1.2f,
            MaximumDegreeOfParallelism: 1);

        // Act
        ScannedRockLodPackageResult result =
            ScannedRockLodPackageBaker.Bake(sourceRoot, outputRoot, options, simplifier);

        // Assert
        var summary = new
        {
            result.SourcePageCount,
            result.OutputPageCount,
            IndexedPages = ScannedRockPageIndexStore.Read(outputRoot).Count,
            Lod0Exists = File.Exists(Path.Combine(
                outputRoot,
                ScannedRockMeshPageStore.RelativePathFor(0, 7, -9))),
            Lod1Exists = File.Exists(Path.Combine(
                outputRoot,
                ScannedRockMeshPageStore.RelativePathFor(1, 7, -9))),
            Lod2Exists = File.Exists(Path.Combine(
                outputRoot,
                ScannedRockMeshPageStore.RelativePathFor(2, 7, -9))),
            MaterialExists = File.Exists(Path.Combine(outputRoot, "20.rtex")),
        };
        summary.Should().BeEquivalentTo(new
        {
            SourcePageCount = 1,
            OutputPageCount = 3,
            IndexedPages = 3,
            Lod0Exists = true,
            Lod1Exists = true,
            Lod2Exists = true,
            MaterialExists = true,
        });
    }

    [Fact]
    public void should_keep_finest_payload_identical_to_source()
    {
        // Arrange
        string sourceRoot = CreateSourcePackage();
        string outputRoot = Path.Combine(root, "output");
        ScannedRockMeshPage source = ReadPage(sourceRoot, lod: 0);

        // Act
        _ = ScannedRockLodPackageBaker.Bake(
            sourceRoot,
            outputRoot,
            new ScannedRockLodPackageOptions(MaximumDegreeOfParallelism: 1),
            new FirstTriangleSimplifier());
        ScannedRockMeshPage output = ReadPage(outputRoot, lod: 0);

        // Assert
        new { output.VertexData, output.Indices }.Should().BeEquivalentTo(
            new { source.VertexData, source.Indices });
    }

    [Fact]
    public void should_not_make_lod2_heavier_when_locked_borders_prevent_target_reduction()
    {
        // Arrange
        string sourceRoot = CreateSourcePackage();
        string outputRoot = Path.Combine(root, "output");
        var options = new ScannedRockLodPackageOptions(
            Lod1TriangleFraction: 0.75f,
            Lod1MaximumErrorMeters: 0.35f,
            Lod2TriangleFraction: 0.25f,
            Lod2MaximumErrorMeters: 1.2f,
            MaximumDegreeOfParallelism: 1);

        // Act
        _ = ScannedRockLodPackageBaker.Bake(
            sourceRoot,
            outputRoot,
            options,
            new RegressingCoarseSimplifier());
        ScannedRockMeshPage lod1 = ReadPage(outputRoot, lod: 1);
        ScannedRockMeshPage lod2 = ReadPage(outputRoot, lod: 2);

        // Assert
        lod2.IndexCount.Should().BeLessThanOrEqualTo(lod1.IndexCount);
    }

    [Fact]
    public void should_keep_reported_error_monotonic_across_lods()
    {
        // Arrange
        string sourceRoot = CreateSourcePackage();
        string outputRoot = Path.Combine(root, "output");
        var options = new ScannedRockLodPackageOptions(
            Lod1TriangleFraction: 0.75f,
            Lod1MaximumErrorMeters: 0.35f,
            Lod2TriangleFraction: 0.25f,
            Lod2MaximumErrorMeters: 1.2f,
            MaximumDegreeOfParallelism: 1);

        // Act
        _ = ScannedRockLodPackageBaker.Bake(
            sourceRoot,
            outputRoot,
            options,
            new RegressingCoarseSimplifier());
        ScannedRockMeshPage lod1 = ReadPage(outputRoot, lod: 1);
        ScannedRockMeshPage lod2 = ReadPage(outputRoot, lod: 2);

        // Assert
        lod2.GeometricError.Should().BeGreaterThanOrEqualTo(lod1.GeometricError);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private string CreateSourcePackage()
    {
        string sourceRoot = Path.Combine(root, "source");
        Directory.CreateDirectory(sourceRoot);
        var page = new ScannedRockMeshPage(
            lod: 0,
            pageX: 7,
            pageY: -9,
            worldMin: new Vector3(100f, 200f, 300f),
            worldExtent: new Vector3(16f, 16f, 8f),
            geometricError: 4f,
            materialPageId: 20,
            vertexData: Enumerable.Range(0, 4 * ScannedRockMeshPage.VertexStrideBytes)
                .Select(value => (byte)value)
                .ToArray(),
            indices: [0, 1, 2, 0, 2, 3]);
        string pagePath = Path.Combine(
            sourceRoot,
            ScannedRockMeshPageStore.RelativePathFor(0, page.PageX, page.PageY));
        Directory.CreateDirectory(Path.GetDirectoryName(pagePath)!);
        using (FileStream stream = File.Create(pagePath))
        {
            ScannedRockMeshPageStore.Write(stream, page);
        }

        File.WriteAllBytes(Path.Combine(sourceRoot, "20.rtex"), [1, 2, 3, 4]);
        File.WriteAllText(Path.Combine(sourceRoot, "manifest.json"), """{"format":"RMP2-test"}""");
        return sourceRoot;
    }

    private static ScannedRockMeshPage ReadPage(string packageRoot, byte lod)
    {
        using FileStream stream = File.OpenRead(Path.Combine(
            packageRoot,
            ScannedRockMeshPageStore.RelativePathFor(lod, 7, -9)));
        return ScannedRockMeshPageStore.Read(stream);
    }

    private sealed class FirstTriangleSimplifier : IScannedRockIndexSimplifier
    {
        public ScannedRockIndexSimplification Simplify(
            ReadOnlySpan<uint> indices,
            ReadOnlySpan<float> positions,
            int vertexCount,
            ScannedRockSimplificationRequest request) =>
            new(indices[..3].ToArray(), request.MaximumGeometricErrorMeters * 0.5f);
    }

    private sealed class RegressingCoarseSimplifier : IScannedRockIndexSimplifier
    {
        private int invocation;

        public ScannedRockIndexSimplification Simplify(
            ReadOnlySpan<uint> indices,
            ReadOnlySpan<float> positions,
            int vertexCount,
            ScannedRockSimplificationRequest request)
        {
            invocation++;
            return invocation == 1
                ? new ScannedRockIndexSimplification(indices[..3].ToArray(), 0.3f)
                : new ScannedRockIndexSimplification(indices.ToArray(), 0.2f);
        }
    }
}
