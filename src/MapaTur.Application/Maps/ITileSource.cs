using MapaTur.Domain.Maps;

namespace MapaTur.Application.Maps;

/// <summary>
/// Port for an offline raster tile source. Implementations expose tile payloads keyed by
/// XYZ coordinate plus a metadata descriptor.
/// </summary>
public interface ITileSource : IDisposable
{
    /// <summary>
    /// Returns the descriptive metadata of the tile source.
    /// </summary>
    /// <returns>Metadata for this source.</returns>
    TileSourceMetadata GetMetadata();

    /// <summary>
    /// Reads a tile payload by XYZ coordinate.
    /// </summary>
    /// <param name="coordinate">XYZ tile coordinate to read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The raw tile bytes, or null if the tile is absent from the source.</returns>
    Task<byte[]?> GetTileAsync(TileCoordinate coordinate, CancellationToken cancellationToken = default);
}