using BruTile.MbTiles;

using MapaTur.Domain.Geography;

using Mapsui;
using Mapsui.Layers;
using Mapsui.Projections;
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
    private const string BasemapLayerPrefix = "mbtiles-basemap-";
    private const string HillshadeLayerName = "mbtiles-hillshade";
    private const double HillshadeOpacity = 0.55;

    /// <inheritdoc />
    public MapBounds? LoadMBTilesArchive(Map map, string archivePath, MBTilesLayerKind kind = MBTilesLayerKind.Basemap)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("MBTiles archive not found.", archivePath);
        }

        // Basemaps stack — each archive becomes its own layer keyed by filename so
        // multiple regional MBTiles (Tatry + Beskidy + Bieszczady etc.) can coexist.
        // Hillshade stays singleton: only one hillshade role at a time.
        string layerName = kind == MBTilesLayerKind.Hillshade
            ? HillshadeLayerName
            : BasemapLayerPrefix + Path.GetFileNameWithoutExtension(archivePath);
        RemoveLayer(map, layerName);

        var tileSource = new MbTilesTileSource(new SQLiteConnectionString(archivePath, storeDateTimeAsTicks: false));
        var tileLayer = new TileLayer(tileSource)
        {
            Name = layerName,
            Opacity = kind == MBTilesLayerKind.Hillshade ? HillshadeOpacity : 1.0,
        };

        int insertIndex = ComputeInsertIndex(map, kind);
        map.Layers.Insert(insertIndex, tileLayer);

        var extent = tileSource.Schema.Extent;
        if (extent.Width <= 0 || extent.Height <= 0)
        {
            return null;
        }

        // Only the FIRST basemap drives the viewport — subsequent regional layers
        // shouldn't yank the camera around. Hillshade never zooms.
        bool isFirstBasemap = kind == MBTilesLayerKind.Basemap && !HasOtherBasemapLayer(map, layerName);
        if (isFirstBasemap)
        {
            map.Navigator.ZoomToBox(new MRect(extent.MinX, extent.MinY, extent.MaxX, extent.MaxY));
        }

        var (swLon, swLat) = SphericalMercator.ToLonLat(extent.MinX, extent.MinY);
        var (neLon, neLat) = SphericalMercator.ToLonLat(extent.MaxX, extent.MaxY);
        try
        {
            return new MapBounds(
                new GeoPoint(Math.Clamp(swLat, -85.0, 85.0), Math.Clamp(swLon, -180.0, 180.0)),
                new GeoPoint(Math.Clamp(neLat, -85.0, 85.0), Math.Clamp(neLon, -180.0, 180.0)));
        }
        catch (ArgumentException)
        {
            // Degenerate extent (corners swapped after clamping) — treat as no bounds.
            return null;
        }
    }

    private static int ComputeInsertIndex(Map map, MBTilesLayerKind kind)
    {
        // Hillshade always goes at the very bottom (index 0). Basemaps go above any
        // existing hillshade layer but below any existing vector overlays (trails,
        // tracks, routes, waypoints) that the user added later. Multiple basemaps
        // stack in load order — each new one goes on top of the previous, so the
        // last loaded "wins" where it overlaps an earlier basemap.
        if (kind == MBTilesLayerKind.Hillshade)
        {
            return 0;
        }

        // Find the last existing basemap (or hillshade if no basemap yet) and insert above it.
        int lastBaseIndex = -1;
        int index = 0;
        foreach (var layer in map.Layers)
        {
            string? name = layer.Name;
            if (name is not null && (name == HillshadeLayerName || name.StartsWith(BasemapLayerPrefix, StringComparison.Ordinal)))
            {
                lastBaseIndex = index;
            }
            index++;
        }
        return lastBaseIndex + 1;
    }

    private static bool HasOtherBasemapLayer(Map map, string excludeName)
    {
        foreach (var layer in map.Layers)
        {
            if (layer.Name is string name
                && name != excludeName
                && name.StartsWith(BasemapLayerPrefix, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
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