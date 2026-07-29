using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.RockBake.Tests;

public sealed class HybridTerrainPilotPackageBakerTests
{
    [Fact]
    public void should_write_verified_rmp3_pages_without_legacy_overlay_files()
    {
        // Arrange
        string output = Path.Combine(Path.GetTempPath(), $"mapatur-rmp3-pilot-{Guid.NewGuid():N}");
        try
        {
            IReadOnlyList<RockMeshTriangle> wall = CreateGridWall(size: 8);
            var options = new HybridTerrainPilotBakeOptions(
                PageSizeMeters: 32f,
                MaximumEdgeMeters: 1.5f,
                SampleAmplitudeMeters: 1f,
                MaximumReliefMeters: 2.8f,
                Seed: 20260729);

            // Act
            HybridTerrainPilotBakeResult result = HybridTerrainPilotPackageBaker.Bake(
                wall,
                static (_, _) => new RockSurfaceSample(1f, 220, 3),
                static _ => new Vector2(0.25f, 0.75f),
                output,
                options);

            // Assert
            Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)
                .Count(path => Path.GetExtension(path) == HybridTerrainMeshPageStore.FileExtension)
                .Should().Be(result.PageCount);
            Directory.EnumerateFiles(output, "*.rmp2", SearchOption.AllDirectories)
                .Should().BeEmpty();
            Directory.EnumerateFiles(output, "*" + HybridTerrainMeshPageStore.FileExtension, SearchOption.AllDirectories)
                .Select(path =>
                {
                    using FileStream stream = File.OpenRead(path);
                    return HybridTerrainMeshPageStore.ReadHeader(stream).Lod;
                })
                .Distinct()
                .Should().Equal((byte)0, (byte)1, (byte)2);
        }
        finally
        {
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
        }
    }

    private static List<RockMeshTriangle> CreateGridWall(int size)
    {
        var result = new List<RockMeshTriangle>(size * size * 2);
        for (int y = 0; y < size; y++)
        {
            for (int z = 0; z < size; z++)
            {
                var a = new Vector3(0f, y, z);
                var b = new Vector3(0f, y + 1, z);
                var c = new Vector3(0f, y, z + 1);
                var d = new Vector3(0f, y + 1, z + 1);
                result.Add(new RockMeshTriangle(a, b, c));
                result.Add(new RockMeshTriangle(b, d, c));
            }
        }

        return result;
    }
}
