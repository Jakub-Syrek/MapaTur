using MapaTur.Domain.Geography;

namespace MapaTur.Application.Terrain;

/// <summary>The tiles a viewport should hold this update: the chosen zoom and the tile set covering the view.</summary>
/// <param name="Zoom">The zoom level chosen under both the resolution target and the tile budget.</param>
/// <param name="Tiles">Tiles (row-major) covering the view bounds at <see cref="Zoom"/>.</param>
public sealed record StreamingTilePlan(int Zoom, IReadOnlyList<DemTileKey> Tiles);

/// <summary>
/// Picks the DEM tiles a viewport should stream, balancing TWO constraints: the target on-screen ground
/// resolution (don't fetch finer than useful) and a hard tile-count budget (don't blow VRAM/bandwidth). It
/// takes the finest zoom that satisfies both — min(zoom-for-resolution, zoom-for-budget) — then returns that
/// zoom's tiles over the view bounds. Pure composition over <see cref="DemTilePlanner"/>; the resulting set
/// feeds <see cref="DemTileResidencyPlanner"/> (load/keep/evict). No I/O, no rendering.
/// </summary>
public static class StreamingTilePlanner
{
    /// <summary>
    /// Plans the desired tile set for <paramref name="viewBounds"/>. <paramref name="targetMetersPerPixel"/>
    /// caps the zoom at the resolution actually useful on screen; <paramref name="maxTiles"/> caps it at what
    /// fits the budget. The chosen zoom is the finer-limited of the two, clamped to
    /// [<paramref name="minZoom"/>, <paramref name="maxZoom"/>].
    /// </summary>
    public static StreamingTilePlan Plan(
        MapBounds viewBounds,
        double targetMetersPerPixel,
        double latitudeDegrees,
        int maxTiles,
        int minZoom = 0,
        int maxZoom = 14)
    {
        int zoomForResolution = DemTilePlanner.ZoomForGroundResolution(targetMetersPerPixel, latitudeDegrees, minZoom, maxZoom);
        int zoomForBudget = DemTilePlanner.ChooseZoomForBudget(viewBounds, maxTiles, minZoom, maxZoom);

        // Finest zoom that satisfies BOTH constraints; never below the floor.
        int zoom = Math.Max(minZoom, Math.Min(zoomForResolution, zoomForBudget));

        IReadOnlyList<DemTileKey> tiles = DemTilePlanner.TilesForBounds(viewBounds, zoom);
        return new StreamingTilePlan(zoom, tiles);
    }
}