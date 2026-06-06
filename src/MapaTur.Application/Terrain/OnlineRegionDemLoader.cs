using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Loads a whole region's elevation as one <see cref="DemRaster"/> by planning the slippy tiles that
/// cover a <see cref="MapBounds"/> at a zoom (<see cref="DemTilePlanner"/>), fetching each through an
/// <see cref="IDemTileSource"/> (e.g. a composite of GUGiK NMT 1 m + global Terrarium), and stitching
/// the results with <see cref="DemTileMosaic"/>. Tiles that come back null (out of coverage / failed)
/// are skipped; the region is null only when no tile could be fetched.
/// </summary>
public sealed class OnlineRegionDemLoader
{
    private readonly IDemTileSource source;

    /// <summary>Initializes the loader over a tile source.</summary>
    public OnlineRegionDemLoader(IDemTileSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        this.source = source;
    }

    /// <summary>
    /// Fetches and stitches the tiles covering <paramref name="bounds"/> at <paramref name="zoom"/>.
    /// Returns <c>null</c> when no tile was available.
    /// </summary>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public async Task<DemRaster?> LoadRegionAsync(MapBounds bounds, int zoom, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DemTileKey> keys = DemTilePlanner.TilesForBounds(bounds, zoom);
        var placed = new List<PlacedDemTile>(keys.Count);

        foreach (DemTileKey key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();

            DemRaster? raster = await this.source.GetTileAsync(key, cancellationToken).ConfigureAwait(false);
            if (raster is not null)
            {
                placed.Add(new PlacedDemTile(key, raster));
            }
        }

        return placed.Count == 0 ? null : DemTileMosaic.Stitch(placed);
    }
}