namespace MapaTur.Domain.Maps;

/// <summary>
/// Raster format of a tile payload.
/// </summary>
public enum TileFormat
{
    /// <summary>Unknown or unsupported format.</summary>
    Unknown = 0,

    /// <summary>PNG raster tile.</summary>
    Png = 1,

    /// <summary>JPEG raster tile.</summary>
    Jpeg = 2,

    /// <summary>WebP raster tile.</summary>
    WebP = 3,
}