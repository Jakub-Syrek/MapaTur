using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class ScannedRockStreamingManagerTests
{
    [Fact]
    public async Task should_start_page_io_without_blocking_update()
    {
        // Arrange
        var pending = new TaskCompletionSource<ScannedRockMeshPage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        ScannedRockPageDescriptor descriptor = Descriptor(2);
        var manager = new ScannedRockStreamingManager(
            [descriptor],
            (_, _) => pending.Task,
            maxResidentBytes: 1024,
            maxConcurrentLoads: 1);

        // Act
        ScannedRockStreamingUpdate update = manager.Update(Options(distance: 101f));

        // Assert
        update.InFlight.Should().Be(1);
        update.ResidentPages.Should().BeEmpty();
        pending.SetResult(Page(2));
        await pending.Task;
    }

    [Fact]
    public async Task should_keep_resident_fallback_until_new_lod_is_ready()
    {
        // Arrange
        var finePending = new TaskCompletionSource<ScannedRockMeshPage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        IReadOnlyList<ScannedRockPageDescriptor> descriptors = [Descriptor(0), Descriptor(1), Descriptor(2)];
        var manager = new ScannedRockStreamingManager(
            descriptors,
            (descriptor, _) => descriptor.Key.Lod == 2
                ? Task.FromResult(Page(2))
                : finePending.Task,
            maxResidentBytes: 4096,
            maxConcurrentLoads: 1);
        manager.Update(Options(distance: 101f));
        await Task.Yield();
        manager.Update(Options(distance: 101f));

        // Act
        ScannedRockStreamingUpdate update = manager.Update(Options(distance: 5f));

        // Assert
        update.ResidentPages.Should().ContainSingle(page => page.Lod == 2);
        finePending.SetResult(Page(0));
        await finePending.Task;
    }

    [Fact]
    public async Task should_replace_fallback_after_desired_lod_finishes_loading()
    {
        // Arrange
        var finePending = new TaskCompletionSource<ScannedRockMeshPage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        IReadOnlyList<ScannedRockPageDescriptor> descriptors = [Descriptor(0), Descriptor(1), Descriptor(2)];
        var manager = new ScannedRockStreamingManager(
            descriptors,
            (descriptor, _) => descriptor.Key.Lod == 2
                ? Task.FromResult(Page(2))
                : finePending.Task,
            maxResidentBytes: 4096,
            maxConcurrentLoads: 1);
        manager.Update(Options(distance: 101f));
        await Task.Yield();
        manager.Update(Options(distance: 101f));
        manager.Update(Options(distance: 5f));
        finePending.SetResult(Page(0));
        await finePending.Task;

        // Act
        ScannedRockStreamingUpdate update = manager.Update(Options(distance: 5f));

        // Assert
        update.ResidentPages.Should().ContainSingle(page => page.Lod == 0);
    }

    [Fact]
    public void should_prioritize_visible_page_before_prefetch_neighbor()
    {
        // Arrange
        var requested = new List<ScannedRockPageKey>();
        ScannedRockPageDescriptor visible = Descriptor(2);
        ScannedRockPageDescriptor neighbor = Descriptor(2, pageX: 1, worldMin: new Vector3(0f, 5000f, 0f));
        var manager = new ScannedRockStreamingManager(
            [neighbor, visible],
            (descriptor, _) =>
            {
                requested.Add(descriptor.Key);
                return new TaskCompletionSource<ScannedRockMeshPage>().Task;
            },
            maxResidentBytes: 4096,
            maxConcurrentLoads: 1);

        // Act
        manager.Update(Options(distance: 101f));

        // Assert
        requested.Should().Equal(visible.Key);
    }

    [Fact]
    public void should_index_page_descriptors_once_for_repeated_updates()
    {
        // Arrange
        var descriptors = new CountingReadOnlyList<ScannedRockPageDescriptor>(
            [Descriptor(2)]);
        var manager = new ScannedRockStreamingManager(
            descriptors,
            (_, _) => new TaskCompletionSource<ScannedRockMeshPage>().Task,
            maxResidentBytes: 4096,
            maxConcurrentLoads: 1);
        int enumerationsAfterConstruction = descriptors.EnumerationCount;

        // Act
        manager.Update(Options(distance: 101f));
        manager.Update(Options(distance: 101f));

        // Assert
        descriptors.EnumerationCount.Should().Be(enumerationsAfterConstruction);
    }

    private static ScannedRockPageSelectionOptions Options(float distance) =>
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
                FarPlane = 100_000f,
            },
            AspectRatio = 1f,
            ViewportHeightPixels = 1000,
            MaxErrorPixels = 2.0,
            HysteresisFraction = 0.25,
            PrefetchPageRing = 1,
        };

    private static ScannedRockPageDescriptor Descriptor(
        byte lod,
        int pageX = 0,
        Vector3? worldMin = null) =>
        new(
            new ScannedRockPageKey(pageX, 0, lod),
            worldMin ?? Vector3.Zero,
            Vector3.One,
            lod switch { 0 => 0.005f, 1 => 0.02f, _ => 0.08f },
            materialPageId: 1,
            vertexCount: 3,
            indexCount: 3,
            path: $"{pageX}-0-{lod}.rmp2");

    private static ScannedRockMeshPage Page(byte lod) =>
        new(
            lod,
            0,
            0,
            Vector3.Zero,
            Vector3.One,
            lod switch { 0 => 0.005f, 1 => 0.02f, _ => 0.08f },
            1,
            new byte[ScannedRockMeshPage.VertexStrideBytes * 3],
            [0, 1, 2]);

    private sealed class CountingReadOnlyList<T>(IReadOnlyList<T> items) : IReadOnlyList<T>
    {
        public int EnumerationCount { get; private set; }

        public int Count => items.Count;

        public T this[int index] => items[index];

        public IEnumerator<T> GetEnumerator()
        {
            EnumerationCount++;
            return items.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
