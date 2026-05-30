namespace MapaTur.Application.Maps;

/// <summary>
/// Port for decoding a raw raster tile payload (PNG/JPEG/WebP bytes as read from an
/// MBTiles archive) into a tightly-packed top-row-first RGBA8 pixel buffer. Implementations
/// live in the Infrastructure / App layer where an image codec (SkiaSharp) is available.
/// </summary>
public interface IRasterTileDecoder
{
    /// <summary>
    /// Decodes the tile payload into an RGBA8 pixel buffer.
    /// </summary>
    /// <param name="encoded">Raw tile bytes (PNG/JPEG/WebP).</param>
    /// <returns>Decoded RGBA pixels.</returns>
    DecodedRasterTile Decode(byte[] encoded);
}

/// <summary>
/// Decoded raster tile: tightly-packed top-row-first RGBA8 (4 bytes per pixel).
/// </summary>
/// <param name="Width">Pixel width.</param>
/// <param name="Height">Pixel height.</param>
/// <param name="Rgba">Pixel buffer of length <c>Width * Height * 4</c>, R,G,B,A byte order.</param>
public sealed record DecodedRasterTile(int Width, int Height, byte[] Rgba);