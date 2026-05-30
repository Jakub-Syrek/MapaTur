namespace MapaTur.App.Services;

/// <summary>
/// Result of scanning the user's machine for pre-bundled / installed map data
/// to be opened automatically on app start.
/// </summary>
/// <param name="BasemapMBTilesPaths">All non-hillshade .mbtiles archives found, in discovery order. They stack as separate layers so multiple regional maps (Tatry, Beskidy, Bieszczady, etc.) compose into one base.</param>
/// <param name="HillshadeMBTilesPath">First .mbtiles archive whose filename suggests it is a hillshade layer, or null.</param>
/// <param name="DemPath">First .dem file found, or null.</param>
/// <param name="TrailsDataPath">First pre-fetched trails file (a saved Overpass JSON response, filename containing "trail"), or null. Lets the app load the whole regional trail set from disk instead of hitting Overpass live.</param>
/// <param name="OrthoTexturePath">First ortho-photo image (.png/.jpg whose filename contains "ortho"), or null. Draped over the 3D terrain by the GPU renderer instead of the hypsometric tint.</param>
/// <param name="OrthoTilePaths">Ortho tiles in row-major order (r0c0, r0c1, … r1c0, …) when a tiled set <c>*ortho*-r{R}-c{C}.png</c> is present; otherwise the single <see cref="OrthoTexturePath"/> as a 1-element list, or null when no ortho exists. The mesh is tiled to match (<see cref="OrthoGridCols"/>×<see cref="OrthoGridRows"/>) so each cell samples its own high-resolution tile.</param>
/// <param name="OrthoGridCols">Number of ortho tile columns (1 for a single image).</param>
/// <param name="OrthoGridRows">Number of ortho tile rows (1 for a single image).</param>
public readonly record struct MapAutoLoadDiscovery(
    IReadOnlyList<string> BasemapMBTilesPaths,
    string? HillshadeMBTilesPath,
    string? DemPath,
    string? TrailsDataPath = null,
    string? OrthoTexturePath = null,
    IReadOnlyList<string>? OrthoTilePaths = null,
    int OrthoGridCols = 1,
    int OrthoGridRows = 1);

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