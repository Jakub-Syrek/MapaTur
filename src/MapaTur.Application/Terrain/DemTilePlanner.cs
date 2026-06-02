using MapaTur.Domain.Geography;

namespace MapaTur.Application.Terrain;

/// <summary>Identifies one slippy-map tile in the DEM/ortho pyramid.</summary>
/// <param name="Zoom">Zoom level.</param>
/// <param name="X">Tile column.</param>
/// <param name="Y">Tile row.</param>
public readonly record struct DemTileKey(int Zoom, int X, int Y);

/// <summary>
/// Decides which DEM tiles to stream for the current view: the tile set covering a geographic extent at
/// a zoom, the zoom that matches a desired on-screen ground resolution (LOD), and the highest zoom that
/// keeps the tile count within a budget. Pure math over <see cref="SlippyTileMath"/>.
/// </summary>
public static class DemTilePlanner
{
    // Web-Mercator ground resolution (m/px) at the equator for a 256-px tile, zoom 0.
    private const double EquatorMetersPerPixelZoom0 = 156543.03392;

    /// <summary>Tiles (in row-major order) whose footprint overlaps <paramref name="bounds"/> at <paramref name="zoom"/>.</summary>
    public static IReadOnlyList<DemTileKey> TilesForBounds(MapBounds bounds, int zoom)
    {

        var (xMin, yMin) = SlippyTileMath.LonLatToTile(bounds.SouthWest.Longitude, bounds.NorthEast.Latitude, zoom);
        var (xMax, yMax) = SlippyTileMath.LonLatToTile(bounds.NorthEast.Longitude, bounds.SouthWest.Latitude, zoom);

        var tiles = new List<DemTileKey>();
        for (int y = yMin; y <= yMax; y++)
        {
            for (int x = xMin; x <= xMax; x++)
            {
                tiles.Add(new DemTileKey(zoom, x, y));
            }
        }

        return tiles;
    }

    /// <summary>Number of tiles covering <paramref name="bounds"/> at <paramref name="zoom"/> (no enumeration).</summary>
    public static long TileCount(MapBounds bounds, int zoom)
    {

        var (xMin, yMin) = SlippyTileMath.LonLatToTile(bounds.SouthWest.Longitude, bounds.NorthEast.Latitude, zoom);
        var (xMax, yMax) = SlippyTileMath.LonLatToTile(bounds.NorthEast.Longitude, bounds.SouthWest.Latitude, zoom);
        return (long)(xMax - xMin + 1) * (yMax - yMin + 1);
    }

    /// <summary>
    /// Highest zoom in [<paramref name="minZoom"/>, <paramref name="maxZoom"/>] whose tile count over
    /// <paramref name="bounds"/> is ≤ <paramref name="maxTiles"/>. Falls back to <paramref name="minZoom"/>
    /// when even that exceeds the budget.
    /// </summary>
    public static int ChooseZoomForBudget(MapBounds bounds, int maxTiles, int minZoom = 0, int maxZoom = 14)
    {
        if (maxTiles < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTiles), "Budget must be at least 1 tile.");
        }

        for (int z = maxZoom; z > minZoom; z--)
        {
            if (TileCount(bounds, z) <= maxTiles)
            {
                return z;
            }
        }

        return minZoom;
    }

    /// <summary>
    /// Zoom whose ground resolution best matches <paramref name="metersPerPixel"/> at the given latitude,
    /// clamped to [<paramref name="minZoom"/>, <paramref name="maxZoom"/>].
    /// </summary>
    public static int ZoomForGroundResolution(double metersPerPixel, double latitudeDegrees, int minZoom = 0, int maxZoom = 14)
    {
        if (metersPerPixel <= 0 || !double.IsFinite(metersPerPixel))
        {
            return maxZoom;
        }

        double latRad = latitudeDegrees * Math.PI / 180.0;
        double z = Math.Log2(EquatorMetersPerPixelZoom0 * Math.Cos(latRad) / metersPerPixel);
        if (!double.IsFinite(z))
        {
            return minZoom;
        }

        return Math.Clamp((int)Math.Round(z), minZoom, maxZoom);
    }
}