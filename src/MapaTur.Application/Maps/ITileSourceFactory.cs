namespace MapaTur.Application.Maps;

/// <summary>
/// Factory that opens tile sources backed by local archive files (e.g. MBTiles).
/// </summary>
public interface ITileSourceFactory
{
    /// <summary>
    /// Opens an offline tile source from a file on disk.
    /// </summary>
    /// <param name="archivePath">Absolute path to the tile archive.</param>
    /// <returns>An open tile source ready for read access.</returns>
    /// <exception cref="FileNotFoundException">Thrown when the archive file does not exist.</exception>
    /// <exception cref="InvalidDataException">Thrown when the archive cannot be parsed.</exception>
    ITileSource OpenFromFile(string archivePath);
}
