namespace MapaTur.App.Services;

/// <summary>
/// Result of scanning the user's machine for pre-bundled / installed map data
/// to be opened automatically on app start.
/// </summary>
/// <param name="BasemapMBTilesPaths">All non-hillshade .mbtiles archives found, in discovery order. They stack as separate layers so multiple regional maps (Tatry, Beskidy, Bieszczady, etc.) compose into one base.</param>
/// <param name="HillshadeMBTilesPath">First .mbtiles archive whose filename suggests it is a hillshade layer, or null.</param>
/// <param name="DemPath">First .dem file found, or null.</param>
public readonly record struct MapAutoLoadDiscovery(
    IReadOnlyList<string> BasemapMBTilesPaths,
    string? HillshadeMBTilesPath,
    string? DemPath);

/// <summary>
/// Discovers map data files (MBTiles archives, DEM rasters) on disk so the app can
/// open them automatically on startup instead of forcing every user to pick files manually.
/// </summary>
public interface IMapAutoLoader
{
    /// <summary>
    /// Scans the configured candidate directories in priority order and returns the
    /// first match per role. Treats files whose name contains "hillshade"
    /// (case-insensitive) as the hillshade candidate, anything else .mbtiles as basemap.
    /// </summary>
    MapAutoLoadDiscovery Discover();
}