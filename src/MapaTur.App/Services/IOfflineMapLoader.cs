using Map = Mapsui.Map;

namespace MapaTur.App.Services;

/// <summary>
/// Loads offline tile archives into a Mapsui <see cref="Map"/> instance.
/// </summary>
public interface IOfflineMapLoader
{
    /// <summary>
    /// Loads the given MBTiles archive as a tile layer on top of the supplied map,
    /// replacing any previously loaded archive layer.
    /// </summary>
    /// <param name="map">The map to mutate.</param>
    /// <param name="archivePath">Absolute path to the .mbtiles file.</param>
    void LoadMBTilesArchive(Map map, string archivePath);
}
