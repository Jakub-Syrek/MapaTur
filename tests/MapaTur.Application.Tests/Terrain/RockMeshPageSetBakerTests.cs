using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class RockMeshPageSetBakerTests
{
    [Fact]
    public void should_partition_steep_source_triangles_into_small_world_pages()
    {
        // Arrange
        var source = new[]
        {
            WallAt(x: 1f, y: 1f),
            WallAt(x: 40f, y: 1f),
        };

        // Act
        IReadOnlyList<RockMeshPage> pages = RockMeshPageSetBaker.Bake(
            source,
            pageSizeMeters: 32f,
            static (_, _) => RockSurfaceSample.Unchanged,
            lods: [2]);

        // Assert
        pages.Select(page => page.PageX).Should().BeEquivalentTo([0, 1]);
    }

    [Fact]
    public void should_emit_every_requested_lod_for_each_occupied_page()
    {
        // Arrange
        var source = new[] { WallAt(x: 1f, y: 1f) };

        // Act
        IReadOnlyList<RockMeshPage> pages = RockMeshPageSetBaker.Bake(
            source,
            pageSizeMeters: 32f,
            static (_, _) => RockSurfaceSample.Unchanged,
            lods: [0, 1, 2]);

        // Assert
        pages.Select(page => page.Lod).Should().Equal(0, 1, 2);
    }

    [Fact]
    public void should_not_create_pages_for_non_rock_ground()
    {
        // Arrange
        var source = new[]
        {
            new RockMeshTriangle(
                Vector3.Zero,
                new Vector3(1f, 0f, 0f),
                new Vector3(0f, 1f, 0f)),
        };

        // Act
        IReadOnlyList<RockMeshPage> pages = RockMeshPageSetBaker.Bake(
            source,
            pageSizeMeters: 32f,
            static (_, _) => RockSurfaceSample.Unchanged,
            lods: [2]);

        // Assert
        pages.Should().BeEmpty();
    }

    private static RockMeshTriangle WallAt(float x, float y) => new(
        new Vector3(x, y, 0f),
        new Vector3(x, y + 1f, 0f),
        new Vector3(x, y, 1f));
}
