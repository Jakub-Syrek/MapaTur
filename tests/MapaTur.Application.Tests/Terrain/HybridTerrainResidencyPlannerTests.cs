using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class HybridTerrainResidencyPlannerTests
{
    [Fact]
    public void should_keep_single_parent_until_every_requested_child_is_resident()
    {
        // Arrange
        HybridTerrainPageKey parent = Key(lod: 1, x: 0, y: 0);
        HybridTerrainPageKey[] children =
        [
            Key(lod: 0, x: 0, y: 0),
            Key(lod: 0, x: 1, y: 0),
            Key(lod: 0, x: 0, y: 1),
            Key(lod: 0, x: 1, y: 1),
        ];
        HashSet<HybridTerrainPageKey> resident = [parent, .. children.Take(3)];

        // Act
        HybridTerrainDrawPlan plan = HybridTerrainResidencyPlanner.Resolve(children, resident);

        // Assert
        plan.Pages.Should().Equal(parent);
    }

    [Fact]
    public void should_replace_parent_atomically_when_every_requested_child_is_resident()
    {
        // Arrange
        HybridTerrainPageKey parent = Key(lod: 1, x: 0, y: 0);
        HybridTerrainPageKey[] children =
        [
            Key(lod: 0, x: 0, y: 0),
            Key(lod: 0, x: 1, y: 0),
            Key(lod: 0, x: 0, y: 1),
            Key(lod: 0, x: 1, y: 1),
        ];
        HashSet<HybridTerrainPageKey> resident = [parent, .. children];

        // Act
        HybridTerrainDrawPlan plan = HybridTerrainResidencyPlanner.Resolve(children, resident);

        // Assert
        plan.Pages.Should().BeEquivalentTo(children);
    }

    [Fact]
    public void should_use_legacy_dem_only_for_uncovered_requested_regions()
    {
        // Arrange
        HybridTerrainPageKey[] requested =
        [
            Key(lod: 0, x: 0, y: 0),
            Key(lod: 0, x: 1, y: 0),
        ];
        HashSet<HybridTerrainPageKey> resident = [requested[0]];

        // Act
        HybridTerrainDrawPlan plan = HybridTerrainResidencyPlanner.Resolve(requested, resident);

        // Assert
        plan.LegacyDemFallbacks.Should().Equal(requested[1]);
    }

    [Fact]
    public void should_find_parent_with_floor_division_for_negative_page_coordinates()
    {
        // Arrange
        HybridTerrainPageKey requested = Key(lod: 0, x: -1, y: -1);
        HybridTerrainPageKey parent = Key(lod: 1, x: -1, y: -1);

        // Act
        HybridTerrainDrawPlan plan = HybridTerrainResidencyPlanner.Resolve(
            [requested],
            new HashSet<HybridTerrainPageKey> { parent });

        // Assert
        plan.Pages.Should().Equal(parent);
    }

    [Fact]
    public void should_reject_requested_cut_with_overlapping_ancestor_and_descendant()
    {
        // Arrange
        HybridTerrainPageKey parent = Key(lod: 1, x: 0, y: 0);
        HybridTerrainPageKey child = Key(lod: 0, x: 0, y: 0);

        // Act
        Action resolve = () => HybridTerrainResidencyPlanner.Resolve(
            [parent, child],
            new HashSet<HybridTerrainPageKey>());

        // Assert
        resolve.Should().Throw<ArgumentException>();
    }

    private static HybridTerrainPageKey Key(byte lod, int x, int y) => new(x, y, lod);
}
