using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class HybridTerrainStreamingManagerTests
{
    [Fact]
    public void should_start_coarsest_fallback_io_before_requested_child_without_blocking_update()
    {
        // Arrange
        var requestedLoads = new List<HybridTerrainPageKey>();
        var pending = new TaskCompletionSource<HybridTerrainMeshPage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        HybridTerrainPageDescriptor[] descriptors =
        [
            Descriptor(lod: 0, x: 1, y: 1),
            Descriptor(lod: 1, x: 0, y: 0),
            Descriptor(lod: 2, x: 0, y: 0),
        ];
        using var manager = new HybridTerrainStreamingManager(
            descriptors,
            (descriptor, _) =>
            {
                requestedLoads.Add(descriptor.Key);
                return pending.Task;
            },
            maxResidentBytes: 4096,
            maxStagingBytes: 4096,
            maxConcurrentLoads: 1);

        // Act
        HybridTerrainStreamingUpdate update = manager.Update([descriptors[0].Key]);

        // Assert
        update.InFlight.Should().Be(1);
        requestedLoads.Should().Equal(descriptors[2].Key);
    }

    [Fact]
    public void should_keep_in_flight_payload_within_staging_budget()
    {
        // Arrange
        var pending = new TaskCompletionSource<HybridTerrainMeshPage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        HybridTerrainPageDescriptor[] descriptors =
        [
            Descriptor(lod: 2, x: 0, y: 0),
            Descriptor(lod: 2, x: 1, y: 0),
        ];
        using var manager = new HybridTerrainStreamingManager(
            descriptors,
            (_, _) => pending.Task,
            maxResidentBytes: 4096,
            maxStagingBytes: descriptors[0].ResidentBytes,
            maxConcurrentLoads: 4);

        // Act
        HybridTerrainStreamingUpdate update = manager.Update(
            descriptors.Select(descriptor => descriptor.Key).ToArray());

        // Assert
        update.StagingBytes.Should().BeLessThanOrEqualTo(descriptors[0].ResidentBytes);
    }

    [Fact]
    public async Task should_keep_loaded_parent_in_staging_until_gpu_upload_is_confirmed()
    {
        // Arrange
        HybridTerrainPageDescriptor child = Descriptor(lod: 0, x: 1, y: 1);
        HybridTerrainPageDescriptor parent = Descriptor(lod: 1, x: 0, y: 0);
        HybridTerrainPageDescriptor root = Descriptor(lod: 2, x: 0, y: 0);
        using var manager = new HybridTerrainStreamingManager(
            [child, parent, root],
            (descriptor, _) => Task.FromResult(Page(descriptor.Key)),
            maxResidentBytes: 4096,
            maxStagingBytes: 4096,
            maxConcurrentLoads: 1);
        manager.Update([child.Key]);
        await Task.Yield();

        // Act
        HybridTerrainStreamingUpdate update = manager.Update([child.Key]);

        // Assert
        update.ReadyForUpload.Should().ContainSingle(page => page.Lod == 2);
    }

    [Fact]
    public async Task should_draw_parent_only_after_gpu_upload_is_confirmed()
    {
        // Arrange
        HybridTerrainPageDescriptor child = Descriptor(lod: 0, x: 1, y: 1);
        HybridTerrainPageDescriptor parent = Descriptor(lod: 1, x: 0, y: 0);
        HybridTerrainPageDescriptor root = Descriptor(lod: 2, x: 0, y: 0);
        using var manager = new HybridTerrainStreamingManager(
            [child, parent, root],
            (descriptor, _) => Task.FromResult(Page(descriptor.Key)),
            maxResidentBytes: 4096,
            maxStagingBytes: 4096,
            maxConcurrentLoads: 1);
        manager.Update([child.Key]);
        await Task.Yield();
        manager.Update([child.Key]);
        manager.ConfirmUploaded(root.Key);

        // Act
        HybridTerrainStreamingUpdate update = manager.Update([child.Key]);

        // Assert
        update.DrawablePages.Should().ContainSingle(page => page.Lod == 2);
    }

    [Fact]
    public async Task should_count_ready_upload_against_staging_budget()
    {
        // Arrange
        HybridTerrainPageDescriptor first = Descriptor(lod: 2, x: 0, y: 0);
        HybridTerrainPageDescriptor second = Descriptor(lod: 2, x: 1, y: 0);
        using var manager = new HybridTerrainStreamingManager(
            [first, second],
            (descriptor, _) => Task.FromResult(Page(descriptor.Key)),
            maxResidentBytes: 4096,
            maxStagingBytes: first.ResidentBytes,
            maxConcurrentLoads: 2);
        manager.Update([first.Key, second.Key]);
        await Task.Yield();

        // Act
        HybridTerrainStreamingUpdate update = manager.Update([first.Key, second.Key]);

        // Assert
        update.StagingBytes.Should().Be(first.ResidentBytes);
    }

    [Fact]
    public void should_reject_catalog_page_larger_than_staging_budget()
    {
        // Arrange
        HybridTerrainPageDescriptor descriptor = Descriptor(lod: 2, x: 0, y: 0);

        // Act
        Action create = () =>
        {
            using var manager = new HybridTerrainStreamingManager(
                [descriptor],
                (_, _) => Task.FromResult(Page(descriptor.Key)),
                maxResidentBytes: 4096,
                maxStagingBytes: descriptor.ResidentBytes - 1,
                maxConcurrentLoads: 1);
        };

        // Assert
        create.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void should_select_screen_space_cut_before_starting_streaming()
    {
        // Arrange
        HybridTerrainPageDescriptor[] hierarchy =
        [
            Descriptor(lod: 0, x: 0, y: 0),
            Descriptor(lod: 1, x: 0, y: 0),
            Descriptor(lod: 2, x: 0, y: 0),
        ];
        using var manager = new HybridTerrainStreamingManager(
            hierarchy,
            (_, _) => new TaskCompletionSource<HybridTerrainMeshPage>().Task,
            maxResidentBytes: 4096,
            maxStagingBytes: 4096,
            maxConcurrentLoads: 1);

        // Act
        HybridTerrainStreamingUpdate update = manager.Update(SelectionOptions(distance: 1000f));

        // Assert
        update.DesiredKeys.Should().ContainSingle(key => key.Lod == 2);
    }

    [Fact]
    public async Task should_reject_loaded_page_whose_identity_differs_from_descriptor()
    {
        // Arrange
        HybridTerrainPageDescriptor descriptor = Descriptor(lod: 2, x: 0, y: 0);
        using var manager = new HybridTerrainStreamingManager(
            [descriptor],
            (_, _) => Task.FromResult(Page(new HybridTerrainPageKey(7, 8, 2))),
            maxResidentBytes: 4096,
            maxStagingBytes: 4096,
            maxConcurrentLoads: 1);
        manager.Update([descriptor.Key]);
        await Task.Yield();

        // Act
        HybridTerrainStreamingUpdate update = manager.Update([descriptor.Key]);

        // Assert
        update.FailedKeys.Should().Equal(descriptor.Key);
    }

    private static HybridTerrainPageDescriptor Descriptor(byte lod, int x, int y)
    {
        float size = 32f * (1 << lod);
        return new HybridTerrainPageDescriptor(
            new HybridTerrainPageKey(x, y, lod),
            new Vector3(x * size, y * size, 1800f),
            new Vector3(size, size, 50f),
            lod switch { 0 => 0.01f, 1 => 0.35f, _ => 1.2f },
            vertexCount: 3,
            indexCount: 3,
            path: $"{x}-{y}-{lod}.rmp3");
    }

    private static HybridTerrainMeshPage Page(HybridTerrainPageKey key)
    {
        float size = 32f * (1 << key.Lod);
        return new HybridTerrainMeshPage(
            key.Lod,
            key.PageX,
            key.PageY,
            new Vector3(key.PageX * size, key.PageY * size, 1800f),
            new Vector3(size, size, 50f),
            key.Lod switch { 0 => 0.01f, 1 => 0.35f, _ => 1.2f },
            new byte[HybridTerrainMeshPage.VertexStrideBytes * 3],
            [0, 1, 2]);
    }

    private static HybridTerrainPageSelectionOptions SelectionOptions(float distance) =>
        new()
        {
            Camera = new Camera3D
            {
                Target = new Vector3(64f, 64f, 1825f),
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
        };
}
