using MapaTur.Application.Terrain;

using Xunit;

namespace MapaTur.Application.Tests.Terrain;

public class CascadeRefreshPolicyTests
{
    // Cascade 0 ≈ 771 m, cascade 2 = 15 000 m at 2048 px — the splits the renderer logs.
    private const int MapSize = 2048;

    [Fact]
    public void should_scale_threshold_with_cascade_texel_size()
    {
        double near = CascadeRefreshPolicy.MoveThresholdMeters(771f, MapSize);
        double far = CascadeRefreshPolicy.MoveThresholdMeters(15000f, MapSize);

        Assert.True(far > near * 10, $"far cascade must tolerate far more drift (near={near}, far={far})");
    }

    [Fact]
    public void should_keep_near_cascade_threshold_sub_meter_scale()
    {
        double near = CascadeRefreshPolicy.MoveThresholdMeters(771f, MapSize);

        Assert.InRange(near, 0.5, 4.0);
    }

    [Fact]
    public void should_refresh_when_camera_moved_past_threshold()
    {
        Assert.True(CascadeRefreshPolicy.ShouldRefresh(
            movedMeters: 100f, sliceFar: 15000f, shadowMapSize: MapSize,
            sunDot: 1f, sceneChanged: false, everRendered: true,
            cascadeIndex: 2, msSinceLastRender: 0));
    }

    [Fact]
    public void should_skip_when_camera_drift_is_below_a_texel()
    {
        Assert.False(CascadeRefreshPolicy.ShouldRefresh(
            movedMeters: 5f, sliceFar: 15000f, shadowMapSize: MapSize,
            sunDot: 1f, sceneChanged: false, everRendered: true,
            cascadeIndex: 2, msSinceLastRender: 0));
    }

    [Fact]
    public void should_refresh_near_cascade_for_the_same_drift_that_far_cascade_ignores()
    {
        const float drift = 5f;

        Assert.True(CascadeRefreshPolicy.ShouldRefresh(drift, 771f, MapSize, 1f, false, true, 0, 0));
        Assert.False(CascadeRefreshPolicy.ShouldRefresh(drift, 15000f, MapSize, 1f, false, true, 2, 0));
    }

    [Fact]
    public void should_always_refresh_when_never_rendered()
    {
        Assert.True(CascadeRefreshPolicy.ShouldRefresh(
            movedMeters: 0f, sliceFar: 15000f, shadowMapSize: MapSize,
            sunDot: 1f, sceneChanged: false, everRendered: false,
            cascadeIndex: 2, msSinceLastRender: 0));
    }

    [Fact]
    public void should_refresh_near_cascade_immediately_when_scene_changed()
    {
        // Freshly streamed terrain must cast near shadows at once, however small the camera drift.
        Assert.True(CascadeRefreshPolicy.ShouldRefresh(
            movedMeters: 0f, sliceFar: 771f, shadowMapSize: MapSize,
            sunDot: 1f, sceneChanged: true, everRendered: true,
            cascadeIndex: 0, msSinceLastRender: 0));
    }

    [Fact]
    public void should_amortise_far_cascade_when_streaming_changes_the_tile_set_every_frame()
    {
        // Continuous streaming flips the tile count every frame; without amortisation that re-rendered ALL
        // cascades every frame (measured: 3.00 cascades/frame during long-distance jumps, shadow up to 200 ms).
        Assert.False(CascadeRefreshPolicy.ShouldRefresh(
            movedMeters: 0f, sliceFar: 15000f, shadowMapSize: MapSize,
            sunDot: 1f, sceneChanged: true, everRendered: true,
            cascadeIndex: 2, msSinceLastRender: 50));
    }

    [Fact]
    public void should_still_refresh_far_cascade_after_its_amortisation_window()
    {
        Assert.True(CascadeRefreshPolicy.ShouldRefresh(
            movedMeters: 0f, sliceFar: 15000f, shadowMapSize: MapSize,
            sunDot: 1f, sceneChanged: true, everRendered: true,
            cascadeIndex: 2, msSinceLastRender: 1000));
    }

    [Fact]
    public void should_give_middle_cascade_a_shorter_window_than_the_far_one()
    {
        Assert.True(CascadeRefreshPolicy.SceneChangeMinIntervalMs(1)
            < CascadeRefreshPolicy.SceneChangeMinIntervalMs(2));
        Assert.Equal(0, CascadeRefreshPolicy.SceneChangeMinIntervalMs(0));
    }

    [Fact]
    public void should_refresh_when_sun_moved_beyond_the_cone()
    {
        Assert.True(CascadeRefreshPolicy.ShouldRefresh(
            movedMeters: 0f, sliceFar: 15000f, shadowMapSize: MapSize,
            sunDot: 0.9999f, sceneChanged: false, everRendered: true,
            cascadeIndex: 2, msSinceLastRender: 0));
    }
}