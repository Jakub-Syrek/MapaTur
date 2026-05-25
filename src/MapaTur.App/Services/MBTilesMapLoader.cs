using BruTile.MbTiles;
using Mapsui;
using Mapsui.Tiling.Layers;
using SQLite;
using Map = Mapsui.Map;

namespace MapaTur.App.Services;

/// <summary>
/// Loads MBTiles archives as Mapsui tile layers using BruTile.MbTiles.
/// </summary>
public sealed class MBTilesMapLoader : IOfflineMapLoader
{
    private const string MBTilesLayerName = "mbtiles";

    /// <inheritdoc />
    public void LoadMBTilesArchive(Map map, string archivePath)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("MBTiles archive not found.", archivePath);
        }

        RemoveExistingMBTilesLayer(map);

        var tileSource = new MbTilesTileSource(new SQLiteConnectionString(archivePath, storeDateTimeAsTicks: false));
        var tileLayer = new TileLayer(tileSource)
        {
            Name = MBTilesLayerName,
        };

        map.Layers.Insert(0, tileLayer);

        if (tileSource.Schema.Extent is { } extent)
        {
            map.Navigator.ZoomToBox(new MRect(extent.MinX, extent.MinY, extent.MaxX, extent.MaxY));
        }
    }

    private static void RemoveExistingMBTilesLayer(Map map)
    {
        var existing = map.Layers.FirstOrDefault(layer => layer.Name == MBTilesLayerName);
        if (existing is not null)
        {
            map.Layers.Remove(existing);
        }
    }
}
