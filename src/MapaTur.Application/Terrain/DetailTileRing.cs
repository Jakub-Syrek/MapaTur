using MapaTur.Domain.Geography;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Selects the high-resolution detail tiles to keep resident around the camera's ground focus for
/// streaming-LOD terrain — an (2·radius+1)² ring of tiles centred on the focus tile, over the always-on
/// 30 m base. Pure tile math (which tiles); the persistent base, background loading/building, double
/// buffering and GPU upload budget live elsewhere. See the LOD architecture plan.
/// </summary>
public static class DetailTileRing
{
    /// <summary>
    /// The slippy tiles in a square ring of half-width <paramref name="radiusTiles"/> centred on the tile
    /// containing <paramref name="focus"/> at <paramref name="zoom"/>. Radius 0 = just the focus tile;
    /// radius 1 = 3×3; radius 2 = 5×5. Edge-of-world tiles are simply included by index (no wrap).
    /// </summary>
    public static IReadOnlyList<DemTileKey> Around(GeoPoint focus, int zoom, int radiusTiles)
    {
        if (radiusTiles < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(radiusTiles), radiusTiles, "Ring radius must be zero or positive.");
        }

        var (fx, fy) = SlippyTileMath.LonLatToTile(focus.Longitude, focus.Latitude, zoom);

        var tiles = new List<DemTileKey>((2 * radiusTiles + 1) * (2 * radiusTiles + 1));
        for (int dy = -radiusTiles; dy <= radiusTiles; dy++)
        {
            for (int dx = -radiusTiles; dx <= radiusTiles; dx++)
            {
                tiles.Add(new DemTileKey(zoom, fx + dx, fy + dy));
            }
        }

        return tiles;
    }
}