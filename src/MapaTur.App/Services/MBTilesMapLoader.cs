using BruTile.MbTiles;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Tiling.Layers;
using SQLite;
using Map = Mapsui.Map;

namespace MapaTur.App.Services;

/// <summary>
/// Loads MBTiles archives as Mapsui tile layers using BruTile.MbTiles. Supports two layer
/// roles (basemap, hillshade) so a shaded relief layer can sit beneath the basemap.
/// </summary>
public sealed class MBTilesMapLoader : IOfflineMapLoader
{
    private const string BasemapLayerName = "mbtiles-basemap";
    private const string HillshadeLayerName = "mbtiles-hillshade";
    private const double HillshadeOpacity = 0.55;

    /// <inheritdoc />
    public void LoadMBTilesArchive(Map map, string archivePath, MBTilesLayerKind kind = MBTilesLayerKind.Basemap)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("MBTiles archive not found.", archivePath);
        }

        string layerName = kind == MBTilesLayerKind.Hillshade ? HillshadeLayerName : BasemapLayerName;
        RemoveLayer(map, layerName);

        var tileSource = new MbTilesTileSource(new SQLiteConnectionString(archivePath, storeDateTimeAsTicks: false));
        var tileLayer = new TileLayer(tileSource)
        {
            Name = layerName,
            Opacity = kind == MBTilesLayerKind.Hillshade ? HillshadeOpacity : 1.0,
        };

        int insertIndex = ComputeInsertIndex(map, kind);
        map.Layers.Insert(insertIndex, tileLayer);

        // Only the basemap drives the viewport — a hillshade is an enrichment of an
        // existing area, not a navigation target.
        if (kind == MBTilesLayerKind.Basemap && tileSource.Schema.Extent is { } extent)
        {
            map.Navigator.ZoomToBox(new MRect(extent.MinX, extent.MinY, extent.MaxX, extent.MaxY));
        }
    }

    private static int ComputeInsertIndex(Map map, MBTilesLayerKind kind)
    {
        // Hillshade always goes at the very bottom (index 0). Basemap goes above any
        // existing hillshade layer but below any existing vector overlays (trails,
        // tracks, routes, waypoints) that the user added later.
        if (kind == MBTilesLayerKind.Hillshade)
        {
            return 0;
        }

        int index = 0;
        foreach (var layer in map.Layers)
        {
            if (layer.Name == HillshadeLayerName)
            {
                return index + 1;
            }
            index++;
        }
        return 0;
    }

    private static void RemoveLayer(Map map, string layerName)
    {
        var existing = map.Layers.FirstOrDefault(layer => layer.Name == layerName);
        if (existing is not null)
        {
            map.Layers.Remove(existing);
        }
    }
}
