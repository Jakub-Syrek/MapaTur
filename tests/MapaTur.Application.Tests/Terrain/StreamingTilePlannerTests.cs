using System.Linq;

using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour of the streaming tile planner — picks the DEM tiles a viewport should hold under TWO constraints at
/// once: the target on-screen ground resolution (don't fetch finer than useful) AND a hard tile-count budget
/// (don't blow VRAM/bandwidth). It chooses the finest zoom that satisfies both, then returns that zoom's tiles
/// over the view bounds. Pure composition over DemTilePlanner; feeds DemTileResidencyPlanner.
/// </summary>
public sealed class StreamingTilePlannerTests
{
    // ~4 km box around Morskie Oko (Tatra).
    private static readonly MapBounds View = new(new GeoPoint(49.177, 20.043), new GeoPoint(49.213, 20.097));
    private const double Latitude = 49.195;
    private const int MinZoom = 8;
    private const int MaxZoom = 16;

    [Fact]
    public void Plan_GenerousBudget_IsLimitedByGroundResolution()
    {
        // 1 m/px target → ~z16 here; a huge budget must NOT force a coarser zoom.
        int resZoom = DemTilePlanner.ZoomForGroundResolution(1.0, Latitude, MinZoom, MaxZoom);

        StreamingTilePlan plan = StreamingTilePlanner.Plan(View, targetMetersPerPixel: 1.0, Latitude, maxTiles: 100_000, MinZoom, MaxZoom);

        plan.Zoom.Should().Be(resZoom);
    }

    [Fact]
    public void Plan_TightBudget_DropsZoomToFitAndStaysWithinBudget()
    {
        int resZoom = DemTilePlanner.ZoomForGroundResolution(1.0, Latitude, MinZoom, MaxZoom);

        StreamingTilePlan plan = StreamingTilePlanner.Plan(View, targetMetersPerPixel: 1.0, Latitude, maxTiles: 4, MinZoom, MaxZoom);

        plan.Zoom.Should().BeLessThan(resZoom); // budget forced a coarser zoom
        DemTilePlanner.TileCount(View, plan.Zoom).Should().BeLessThanOrEqualTo(4);
    }

    [Fact]
    public void Plan_TilesMatchChosenZoomOverTheViewBounds()
    {
        StreamingTilePlan plan = StreamingTilePlanner.Plan(View, targetMetersPerPixel: 1.0, Latitude, maxTiles: 64, MinZoom, MaxZoom);

        plan.Tiles.Should().BeEquivalentTo(DemTilePlanner.TilesForBounds(View, plan.Zoom));
        plan.Tiles.Should().OnlyContain(t => t.Zoom == plan.Zoom);
    }

    [Fact]
    public void Plan_NeverExceedsMaxZoom()
    {
        // Absurdly fine target — must clamp to MaxZoom, not overshoot.
        StreamingTilePlan plan = StreamingTilePlanner.Plan(View, targetMetersPerPixel: 0.01, Latitude, maxTiles: 100_000, MinZoom, MaxZoom);

        plan.Zoom.Should().BeLessThanOrEqualTo(MaxZoom);
    }

    [Fact]
    public void Plan_NeverBelowMinZoom()
    {
        // Tiny budget + coarse target: still clamped at MinZoom.
        StreamingTilePlan plan = StreamingTilePlanner.Plan(View, targetMetersPerPixel: 10_000.0, Latitude, maxTiles: 1, MinZoom, MaxZoom);

        plan.Zoom.Should().BeGreaterThanOrEqualTo(MinZoom);
    }
}