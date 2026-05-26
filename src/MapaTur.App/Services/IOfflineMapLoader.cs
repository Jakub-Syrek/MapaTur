using Map = Mapsui.Map;

namespace MapaTur.App.Services;

/// <summary>
/// Role of an MBTiles layer on the map. Layer order is decided by this role so the basemap
/// always sits on top of the hillshade.
/// </summary>
public enum MBTilesLayerKind
{
    /// <summary>Standard raster basemap (streets, terrain, etc.). Drawn on top.</summary>
    Basemap = 0,

    /// <summary>Shaded relief / hillshade. Drawn underneath the basemap.</summary>
    Hillshade = 1,
}

/// <summary>
/// Loads offline tile archives into a Mapsui <see cref="Map"/> instance.
/// </summary>
public interface IOfflineMapLoader
{
    /// <summary>
    /// Loads the given MBTiles archive as a tile layer on top of the supplied map, with the
    /// supplied role determining draw order. Replaces any previously loaded layer of the
    /// same kind.
    /// </summary>
    /// <param name="map">The map to mutate.</param>
    /// <param name="archivePath">Absolute path to the .mbtiles file.</param>
    /// <param name="kind">Layer role. Defaults to <see cref="MBTilesLayerKind.Basemap"/>.</param>
    void LoadMBTilesArchive(Map map, string archivePath, MBTilesLayerKind kind = MBTilesLayerKind.Basemap);
}
