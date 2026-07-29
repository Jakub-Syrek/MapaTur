using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class HybridTerrainPageSelectorTests
{
    [Fact]
    public void should_select_coarsest_root_when_its_error_is_below_pixel_budget()
    {
        // Arrange
        HybridTerrainPageDescriptor[] hierarchy = SparseHierarchy();

        // Act
        IReadOnlyList<HybridTerrainPageSelection> selection = HybridTerrainPageSelector.Select(
            hierarchy,
            Options(distance: 1000f));

        // Assert
        selection.Should().ContainSingle().Which.Descriptor.Key.Lod.Should().Be(2);
    }

    [Fact]
    public void should_refine_to_leaf_when_parent_errors_exceed_pixel_budget()
    {
        // Arrange
        HybridTerrainPageDescriptor[] hierarchy = SparseHierarchy();

        // Act
        IReadOnlyList<HybridTerrainPageSelection> selection = HybridTerrainPageSelector.Select(
            hierarchy,
            Options(distance: 100f));

        // Assert
        selection.Should().ContainSingle().Which.Descriptor.Key.Lod.Should().Be(0);
    }

    [Fact]
    public void should_keep_previous_parent_inside_refinement_hysteresis_band()
    {
        // Arrange
        HybridTerrainPageDescriptor[] hierarchy = SparseHierarchy();
        var previous = new HashSet<HybridTerrainPageKey> { hierarchy.Single(page => page.Key.Lod == 1).Key };

        // Act
        IReadOnlyList<HybridTerrainPageSelection> selection = HybridTerrainPageSelector.Select(
            hierarchy,
            Options(distance: 160f, previous));

        // Assert
        selection.Should().ContainSingle().Which.Descriptor.Key.Lod.Should().Be(1);
    }

    [Fact]
    public void should_keep_previous_child_inside_coarsening_hysteresis_band()
    {
        // Arrange
        HybridTerrainPageDescriptor[] hierarchy = SparseHierarchy();
        var previous = new HashSet<HybridTerrainPageKey> { hierarchy.Single(page => page.Key.Lod == 0).Key };

        // Act
        IReadOnlyList<HybridTerrainPageSelection> selection = HybridTerrainPageSelector.Select(
            hierarchy,
            Options(distance: 195f, previous));

        // Assert
        selection.Should().ContainSingle().Which.Descriptor.Key.Lod.Should().Be(0);
    }

    [Fact]
    public void should_prefetch_adjacent_root_before_it_enters_frustum()
    {
        // Arrange
        HybridTerrainPageDescriptor visible = Root(
            x: 0,
            y: 0,
            worldMin: new Vector3(-1f, -1f, -1f));
        HybridTerrainPageDescriptor neighbor = Root(
            x: 1,
            y: 0,
            worldMin: new Vector3(-1f, 2000f, -1f));

        // Act
        IReadOnlyList<HybridTerrainPageSelection> selection = HybridTerrainPageSelector.Select(
            [visible, neighbor],
            Options(distance: 1000f) with { PrefetchRootRing = 1 });

        // Assert
        selection.Should().Contain(item =>
            item.Descriptor.Key == neighbor.Key
            && !item.IsVisible);
    }

    [Fact]
    public void should_return_non_overlapping_quadtree_cut()
    {
        // Arrange
        HybridTerrainPageDescriptor[] hierarchy = FullHierarchy();

        // Act
        IReadOnlyList<HybridTerrainPageSelection> selection = HybridTerrainPageSelector.Select(
            hierarchy,
            Options(distance: 100f));

        // Assert
        Action validate = () => HybridTerrainResidencyPlanner.Resolve(
            selection.Select(item => item.Descriptor.Key).ToArray(),
            new HashSet<HybridTerrainPageKey>());
        validate.Should().NotThrow();
    }

    private static HybridTerrainPageSelectionOptions Options(
        float distance,
        IReadOnlySet<HybridTerrainPageKey>? previous = null) =>
        new()
        {
            Camera = new Camera3D
            {
                Target = Vector3.Zero,
                Distance = distance,
                AzimuthRadians = 0f,
                PitchRadians = 0f,
                FieldOfViewYRadians = MathF.PI / 2f,
                NearPlane = 0.1f,
                FarPlane = 10_000f,
            },
            AspectRatio = 1f,
            ViewportHeightPixels = 1000,
            MaxErrorPixels = 1.0,
            HysteresisFraction = 0.25,
            PrefetchRootRing = 0,
            PreviousSelection = previous,
        };

    private static HybridTerrainPageDescriptor[] SparseHierarchy() =>
    [
        Descriptor(lod: 0, x: 0, y: 0, error: 0.01f),
        Descriptor(lod: 1, x: 0, y: 0, error: 0.35f),
        Descriptor(lod: 2, x: 0, y: 0, error: 1.2f),
    ];

    private static HybridTerrainPageDescriptor[] FullHierarchy()
    {
        var pages = new List<HybridTerrainPageDescriptor>
        {
            Descriptor(lod: 2, x: 0, y: 0, error: 1.2f),
        };
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 2; x++)
            {
                pages.Add(Descriptor(lod: 1, x, y, error: 0.35f));
                for (int childY = 0; childY < 2; childY++)
                {
                    for (int childX = 0; childX < 2; childX++)
                    {
                        pages.Add(Descriptor(
                            lod: 0,
                            x: (x * 2) + childX,
                            y: (y * 2) + childY,
                            error: 0.01f));
                    }
                }
            }
        }

        return pages.ToArray();
    }

    private static HybridTerrainPageDescriptor Root(int x, int y, Vector3 worldMin) =>
        new(
            new HybridTerrainPageKey(x, y, 2),
            worldMin,
            new Vector3(2f),
            geometricError: 1.2f,
            vertexCount: 3,
            indexCount: 3,
            path: $"{x}-{y}-2.rmp3");

    private static HybridTerrainPageDescriptor Descriptor(byte lod, int x, int y, float error) =>
        new(
            new HybridTerrainPageKey(x, y, lod),
            new Vector3(-1f, -1f, -1f),
            new Vector3(2f),
            error,
            vertexCount: 3,
            indexCount: 3,
            path: $"{x}-{y}-{lod}.rmp3");
}
