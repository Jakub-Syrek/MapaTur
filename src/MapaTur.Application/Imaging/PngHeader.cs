namespace MapaTur.Application.Imaging;

/// <summary>
/// Minimal PNG IHDR sniffer: reads the image's pixel dimensions from the first 24 bytes of the file
/// (signature + IHDR chunk header + big-endian width/height) without decoding anything. Used by the ortho
/// tile-set discovery to compare candidate sets by REAL resolution, so a lower-res copy of the same set
/// (e.g. extracted from an offline package into <c>maps/</c>) can never shadow the full-resolution set in
/// <c>dem/</c> just because its directory happens to be probed first.
/// </summary>
public static class PngHeader
{
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>
    /// Parses PNG pixel dimensions from <paramref name="header"/> (at least the file's first 24 bytes).
    /// Returns false — never throws — for anything that is not a well-formed PNG signature + IHDR start,
    /// or whose encoded dimensions are not both positive.
    /// </summary>
    /// <param name="header">The file's leading bytes; only the first 24 are examined.</param>
    /// <param name="width">Image width in pixels (0 on failure).</param>
    /// <param name="height">Image height in pixels (0 on failure).</param>
    public static bool TryReadDimensions(ReadOnlySpan<byte> header, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (header.Length < 24 || !header[..8].SequenceEqual(Signature))
        {
            return false;
        }

        // The first chunk of a valid PNG is always IHDR; its 4-byte type sits at offset 12.
        if (header[12] != (byte)'I' || header[13] != (byte)'H' || header[14] != (byte)'D' || header[15] != (byte)'R')
        {
            return false;
        }

        width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
        height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
        if (width <= 0 || height <= 0)
        {
            width = 0;
            height = 0;
            return false;
        }

        return true;
    }
}