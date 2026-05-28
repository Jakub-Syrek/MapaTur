using MapaTur.Domain.Geography;

namespace MapaTur.Domain.Maps;

/// <summary>
/// Descriptive metadata for a tile source (typically read from MBTiles metadata table).
/// </summary>
/// <param name="Name">Human-readable name of the tile set.</param>
/// <param name="Format">Raster format of the tiles.</param>
/// <param name="MinZoomLevel">Minimum zoom level available.</param>
/// <param name="MaxZoomLevel">Maximum zoom level available.</param>
/// <param name="Bounds">Geographic coverage of the tile set, if specified.</param>
/// <param name="Attribution">Attribution text required by the data provider.</param>
public sealed record TileSourceMetadata(
    string Name,
    TileFormat Format,
    int MinZoomLevel,
    int MaxZoomLevel,
    MapBounds? Bounds,
    string? Attribution);