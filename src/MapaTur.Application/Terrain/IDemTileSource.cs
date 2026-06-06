using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Terrain;

/// <summary>
/// A source of Digital Elevation Model tiles addressed by slippy <see cref="DemTileKey"/>.
/// Implementations fetch (and typically cache) the elevation grid for a tile and decode it into a
/// <see cref="DemRaster"/> of metres above sea level.
///
/// Contract: <see cref="GetTileAsync"/> returns <c>null</c> when the tile is unavailable from this
/// source (out of coverage, network/HTTP failure, decode error) rather than throwing — so a
/// <see cref="CompositeDemTileSource"/> can fall through to the next source. Cancellation is honoured
/// and may surface as an <see cref="OperationCanceledException"/>.
/// </summary>
public interface IDemTileSource
{
    /// <summary>
    /// Returns the elevation raster for <paramref name="key"/>, or <c>null</c> if this source cannot
    /// provide it (out of coverage or a transient/permanent fetch failure).
    /// </summary>
    Task<DemRaster?> GetTileAsync(DemTileKey key, CancellationToken cancellationToken = default);
}