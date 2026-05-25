using MapaTur.Application.Maps;

namespace MapaTur.Infrastructure.Maps.MBTiles;

/// <summary>
/// Factory that opens MBTiles archive files as tile sources.
/// </summary>
public sealed class MBTilesTileSourceFactory : ITileSourceFactory
{
    /// <inheritdoc />
    public ITileSource OpenFromFile(string archivePath)
    {
        return new MBTilesTileSource(archivePath);
    }
}
