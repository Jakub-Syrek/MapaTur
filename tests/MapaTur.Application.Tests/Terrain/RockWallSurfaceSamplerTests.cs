using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class RockWallSurfaceSamplerTests
{
    [Fact]
    public void should_sample_dense_scan_vertices_without_per_query_managed_allocations()
    {
        // Arrange
        var wallPoints = new List<Vector3>();
        for (int z = -8; z <= 8; z++)
        {
            for (int x = -8; x <= 8; x++)
            {
                wallPoints.Add(new Vector3(x * 0.5f, 2f + (x * 0.01f), z * 0.5f));
            }
        }

        var sampler = new RockWallSurfaceSampler(
            wallPoints,
            Vector3.UnitY,
            cellSizeMeters: 0.5f);
        _ = sampler.SamplePlaneCoordinate(Vector3.Zero);

        // Act
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 1_000; index++)
        {
            _ = sampler.SamplePlaneCoordinate(new Vector3((index % 7) * 0.03f, 0f, 0f));
        }

        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        allocatedBytes.Should().BeLessThan(1024);
    }
}
