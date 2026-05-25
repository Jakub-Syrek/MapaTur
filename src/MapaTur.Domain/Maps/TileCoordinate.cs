namespace MapaTur.Domain.Maps;

/// <summary>
/// XYZ tile coordinate (slippy map convention, Google/OSM scheme).
/// </summary>
/// <param name="ZoomLevel">Zoom level (z), 0 is the whole world.</param>
/// <param name="Column">Column index (x), 0 starts at the west edge.</param>
/// <param name="Row">Row index (y), 0 starts at the north edge in XYZ scheme.</param>
public readonly record struct TileCoordinate(int ZoomLevel, int Column, int Row)
{
    /// <summary>
    /// Maximum supported zoom level.
    /// </summary>
    public const int MaxZoomLevel = 22;

    /// <summary>
    /// Converts this XYZ coordinate to the TMS (Tile Map Service) row used by MBTiles.
    /// TMS flips the Y axis so row 0 is at the south edge.
    /// </summary>
    /// <returns>TMS row index.</returns>
    public int ToTmsRow()
    {
        return (1 << ZoomLevel) - 1 - Row;
    }

    /// <summary>
    /// Creates an XYZ coordinate from a TMS row value (as stored in MBTiles).
    /// </summary>
    /// <param name="zoomLevel">Zoom level.</param>
    /// <param name="column">Column index.</param>
    /// <param name="tmsRow">TMS row index.</param>
    /// <returns>An XYZ tile coordinate.</returns>
    public static TileCoordinate FromTms(int zoomLevel, int column, int tmsRow)
    {
        int xyzRow = (1 << zoomLevel) - 1 - tmsRow;
        return new TileCoordinate(zoomLevel, column, xyzRow);
    }
}
