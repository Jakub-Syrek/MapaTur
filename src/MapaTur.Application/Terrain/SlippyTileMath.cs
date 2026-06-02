namespace MapaTur.Application.Terrain;

/// <summary>
/// Web-Mercator "slippy map" tile coordinate math (the XYZ scheme used by OSM / Esri / terrarium DEM
/// tiles). Converts between geographic coordinates and tile indices at a zoom level, and back to the
/// tile's geographic bounds — used to pick which DEM / ortho tiles cover the current viewport.
/// </summary>
public static class SlippyTileMath
{
    /// <summary>Number of tiles per axis at <paramref name="zoom"/> (2^zoom).</summary>
    public static int TilesPerAxis(int zoom)
    {
        if (zoom < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(zoom), "Zoom must be non-negative.");
        }

        return 1 << zoom;
    }

    /// <summary>
    /// Returns the (X, Y) tile index containing the given lon/lat at <paramref name="zoom"/>. Indices are
    /// clamped to the valid <c>[0, 2^zoom − 1]</c> range so edge coordinates never fall off the grid.
    /// </summary>
    public static (int X, int Y) LonLatToTile(double lon, double lat, int zoom)
    {
        int n = TilesPerAxis(zoom);
        double latRad = lat * Math.PI / 180.0;

        double xf = (lon + 180.0) / 360.0 * n;
        double yf = (1.0 - (Math.Asinh(Math.Tan(latRad)) / Math.PI)) / 2.0 * n;

        int x = Math.Clamp((int)Math.Floor(xf), 0, n - 1);
        int y = Math.Clamp((int)Math.Floor(yf), 0, n - 1);
        return (x, y);
    }

    /// <summary>
    /// Returns the geographic bounds (degrees) of tile (<paramref name="x"/>, <paramref name="y"/>) at
    /// <paramref name="zoom"/>: west/south/east/north.
    /// </summary>
    public static (double West, double South, double East, double North) TileBounds(int x, int y, int zoom)
    {
        int n = TilesPerAxis(zoom);

        double west = ((double)x / n * 360.0) - 180.0;
        double east = ((double)(x + 1) / n * 360.0) - 180.0;
        double north = TileLatitude(y, n);
        double south = TileLatitude(y + 1, n);
        return (west, south, east, north);
    }

    private static double TileLatitude(int y, int n)
    {
        double a = Math.PI * (1.0 - (2.0 * y / n));
        return Math.Atan(Math.Sinh(a)) * 180.0 / Math.PI;
    }
}