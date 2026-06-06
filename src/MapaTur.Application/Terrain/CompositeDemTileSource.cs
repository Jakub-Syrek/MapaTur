using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Terrain;

/// <summary>
/// A <see cref="IDemTileSource"/> that composes several inner sources as a Chain of Responsibility:
/// it queries them in the given order and returns the first non-null <see cref="DemRaster"/>. This is
/// how a high-resolution regional source (GUGiK NMT 1 m, Poland) is layered in front of the global
/// Terrarium fallback — the sharp source is tried first and the global one only fills the gaps where
/// the regional source has no coverage (returns null).
/// </summary>
public sealed class CompositeDemTileSource : IDemTileSource
{
    private readonly IDemTileSource[] sources;

    /// <summary>
    /// Initializes the composite with its ordered inner sources. The first source is preferred; later
    /// sources are consulted only when the earlier ones return <c>null</c> for a tile.
    /// </summary>
    /// <param name="sources">One or more sources, highest priority first.</param>
    /// <exception cref="ArgumentException">No sources were supplied.</exception>
    public CompositeDemTileSource(params IDemTileSource[] sources)
    {
        if (sources is null || sources.Length == 0)
        {
            throw new ArgumentException("At least one DEM tile source is required.", nameof(sources));
        }

        this.sources = (IDemTileSource[])sources.Clone();
    }

    /// <inheritdoc />
    public async Task<DemRaster?> GetTileAsync(DemTileKey key, CancellationToken cancellationToken = default)
    {
        foreach (IDemTileSource source in this.sources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            DemRaster? raster = await source.GetTileAsync(key, cancellationToken).ConfigureAwait(false);
            if (raster is not null)
            {
                return raster;
            }
        }

        return null;
    }
}