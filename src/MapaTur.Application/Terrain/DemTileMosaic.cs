using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Terrain;

/// <summary>A DEM tile raster placed at its slippy <see cref="DemTileKey"/> for mosaicking.</summary>
public sealed record PlacedDemTile(DemTileKey Key, DemRaster Raster);

/// <summary>
/// Assembles a set of per-tile <see cref="DemRaster"/>s (one zoom level, as returned by an
/// <see cref="IDemTileSource"/> for a region) into a single north-up raster the mesh builder can
/// consume. Tiles are positioned by their X/Y relative to the north-west-most tile; any cell with no
/// supplied tile is filled with the NoData sentinel so the mesh treats it as a hole. The 1-sample seam
/// where adjacent slippy tiles share an edge is left as-is (negligible at render scale).
/// </summary>
public static class DemTileMosaic
{
    /// <summary>Stitches the tiles into one raster.</summary>
    /// <exception cref="ArgumentException">No tiles, or tiles with mixed dimensions/zoom.</exception>
    public static DemRaster Stitch(IReadOnlyList<PlacedDemTile> tiles)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        if (tiles.Count == 0)
        {
            throw new ArgumentException("At least one tile is required.", nameof(tiles));
        }

        int zoom = tiles[0].Key.Zoom;
        int tileW = tiles[0].Raster.Columns;
        int tileH = tiles[0].Raster.Rows;
        float noData = tiles[0].Raster.NoDataValue;

        foreach (PlacedDemTile tile in tiles)
        {
            if (tile.Key.Zoom != zoom)
            {
                throw new ArgumentException("All tiles must share one zoom level.", nameof(tiles));
            }

            if (tile.Raster.Columns != tileW || tile.Raster.Rows != tileH)
            {
                throw new ArgumentException("All tiles must have identical dimensions.", nameof(tiles));
            }
        }

        int minX = tiles.Min(t => t.Key.X);
        int maxX = tiles.Max(t => t.Key.X);
        int minY = tiles.Min(t => t.Key.Y);
        int maxY = tiles.Max(t => t.Key.Y);

        int gridCols = maxX - minX + 1;
        int gridRows = maxY - minY + 1;
        int columns = gridCols * tileW;
        int rows = gridRows * tileH;

        var samples = new float[columns * rows];
        Array.Fill(samples, noData);

        foreach (PlacedDemTile tile in tiles)
        {
            int colOffset = (tile.Key.X - minX) * tileW;
            int rowOffset = (tile.Key.Y - minY) * tileH;
            float[] src = tile.Raster.Samples;

            for (int r = 0; r < tileH; r++)
            {
                int destRow = rowOffset + r;
                int destStart = (destRow * columns) + colOffset;
                int srcStart = r * tileW;
                Array.Copy(src, srcStart, samples, destStart, tileW);
            }
        }

        // North-west corner comes from the (minX, minY) tile; south-east from (maxX, maxY).
        var (west, _, _, north) = SlippyTileMath.TileBounds(minX, minY, zoom);
        var (_, south, east, _) = SlippyTileMath.TileBounds(maxX, maxY, zoom);
        var bounds = new MapBounds(new GeoPoint(south, west), new GeoPoint(north, east));

        return new DemRaster(columns, rows, bounds, samples, noData);
    }
}