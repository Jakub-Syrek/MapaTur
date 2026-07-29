using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class ScannedRockPageSelectorTests
{
    [Fact]
    public void should_choose_lod_from_projected_geometric_error()
    {
        // Arrange
        IReadOnlyList<ScannedRockPageDescriptor> pages = ThreeLods(0, 0, Vector3.Zero);
        Camera3D camera = CameraAtDistance(101f);
        var options = Options(camera);

        // Act
        ScannedRockPageSelection selected = ScannedRockPageSelector.Select(pages, options).Single();

        // Assert
        selected.Descriptor.Key.Lod.Should().Be(2);
    }

    [Fact]
    public void should_keep_previous_lod_inside_hysteresis_band()
    {
        // Arrange
        IReadOnlyList<ScannedRockPageDescriptor> pages = ThreeLods(0, 0, Vector3.Zero);
        Camera3D camera = CameraAtDistance(20f);
        var previous = new HashSet<ScannedRockPageKey> { new(0, 0, 2) };
        var options = Options(camera) with { PreviousSelection = previous };

        // Act
        ScannedRockPageSelection selected = ScannedRockPageSelector.Select(pages, options).Single();

        // Assert
        selected.Descriptor.Key.Lod.Should().Be(2);
    }

    [Fact]
    public void should_switch_to_finer_lod_after_crossing_hysteresis_band()
    {
        // Arrange
        IReadOnlyList<ScannedRockPageDescriptor> pages = ThreeLods(0, 0, Vector3.Zero);
        Camera3D camera = CameraAtDistance(14f);
        var previous = new HashSet<ScannedRockPageKey> { new(0, 0, 2) };
        var options = Options(camera) with { PreviousSelection = previous };

        // Act
        ScannedRockPageSelection selected = ScannedRockPageSelector.Select(pages, options).Single();

        // Assert
        selected.Descriptor.Key.Lod.Should().Be(1);
    }

    [Fact]
    public void should_prefetch_one_spatial_ring_beyond_visible_pages()
    {
        // Arrange
        var pages = new List<ScannedRockPageDescriptor>();
        pages.AddRange(ThreeLods(0, 0, Vector3.Zero));
        pages.AddRange(ThreeLods(1, 0, new Vector3(0f, 5000f, 0f)));
        pages.AddRange(ThreeLods(3, 0, new Vector3(0f, 8000f, 0f)));
        var options = Options(CameraAtDistance(101f));

        // Act
        IReadOnlyList<ScannedRockPageSelection> selected = ScannedRockPageSelector.Select(pages, options);

        // Assert
        selected.Select(item => (item.Descriptor.Key.PageX, item.IsVisible))
            .Should()
            .BeEquivalentTo([(0, true), (1, false)]);
    }

    [Fact]
    public void should_prune_spatially_distant_groups_before_leaf_frustum_tests()
    {
        // Arrange
        ScannedRockPageDescriptor[] pages = Enumerable.Range(0, 4096)
            .Select(index => Descriptor(
                lod: 0,
                pageX: index,
                pageY: 0,
                worldMin: index == 0 ? Vector3.Zero : new Vector3(1000f + (index * 2f), 0f, 0f),
                geometricError: 0.005f))
            .ToArray();
        var options = Options(CameraAtDistance(101f)) with { PrefetchPageRing = 0 };

        // Act
        ScannedRockPageSelectionDiagnostics diagnostics =
            ScannedRockPageSelector.SelectWithDiagnostics(pages, options).Diagnostics;

        // Assert
        diagnostics.PageGroupTests.Should().BeLessThan(pages.Length / 16);
    }

    [Fact]
    public void should_return_same_visible_groups_as_brute_force_frustum_culling()
    {
        // Arrange
        ScannedRockPageDescriptor[] pages = Enumerable.Range(-24, 49)
            .SelectMany(x => Enumerable.Range(-24, 49)
                .Select(y => Descriptor(
                    lod: 0,
                    pageX: x,
                    pageY: y,
                    worldMin: new Vector3(x * 40f, y * 40f, ((x + y) % 7) * 15f),
                    geometricError: 0.005f)))
            .ToArray();
        var camera = new Camera3D
        {
            Target = new Vector3(50f, -80f, 40f),
            Distance = 480f,
            AzimuthRadians = 0.63f,
            PitchRadians = 0.28f,
            FieldOfViewYRadians = MathF.PI / 3f,
            NearPlane = 1f,
            FarPlane = 1600f,
        };
        var options = Options(camera) with { PrefetchPageRing = 0 };
        Matrix4x4 viewProjection = camera.BuildViewProjection(options.AspectRatio);
        ScannedRockPageKey[] expected = pages
            .Where(page => FrustumCuller.IsAabbVisible(
                viewProjection,
                page.WorldMin,
                page.WorldMax))
            .Select(page => page.Key)
            .ToArray();

        // Act
        ScannedRockPageKey[] actual = ScannedRockPageSelector.Select(pages, options)
            .Select(item => item.Descriptor.Key)
            .ToArray();

        // Assert
        actual.Should().BeEquivalentTo(expected);
    }

    private static ScannedRockPageSelectionOptions Options(Camera3D camera) =>
        new()
        {
            Camera = camera,
            AspectRatio = 1f,
            ViewportHeightPixels = 1000,
            MaxErrorPixels = 2.0,
            HysteresisFraction = 0.25,
            PrefetchPageRing = 1,
        };

    private static Camera3D CameraAtDistance(float distance) =>
        new()
        {
            Target = Vector3.Zero,
            Distance = distance,
            AzimuthRadians = 0f,
            PitchRadians = 0f,
            FieldOfViewYRadians = MathF.PI / 2f,
            NearPlane = 0.1f,
            FarPlane = 100_000f,
        };

    private static IReadOnlyList<ScannedRockPageDescriptor> ThreeLods(
        int pageX,
        int pageY,
        Vector3 worldMin) =>
        [
            Descriptor(0, pageX, pageY, worldMin, 0.005f),
            Descriptor(1, pageX, pageY, worldMin, 0.02f),
            Descriptor(2, pageX, pageY, worldMin, 0.08f),
        ];

    private static ScannedRockPageDescriptor Descriptor(
        byte lod,
        int pageX,
        int pageY,
        Vector3 worldMin,
        float geometricError) =>
        new(
            new ScannedRockPageKey(pageX, pageY, lod),
            worldMin,
            Vector3.One,
            geometricError,
            materialPageId: 1,
            vertexCount: 3,
            indexCount: 3,
            path: $"{pageX}-{pageY}-{lod}.rmp2");
}
