using MapaTur.Application.Maps;

using SkiaSharp;

namespace MapaTur.App.Services;

/// <summary>
/// SkiaSharp-backed implementation of <see cref="IRasterTileDecoder"/>. Decodes raw
/// PNG/JPEG/WebP tile bytes pulled from an MBTiles archive into a tightly-packed
/// top-row-first RGBA8 buffer ready for upload as a GL texture.
/// </summary>
public sealed class SkiaRasterTileDecoder : IRasterTileDecoder
{
    /// <inheritdoc />
    public DecodedRasterTile Decode(byte[] encoded)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        if (encoded.Length == 0)
        {
            throw new ArgumentException("Tile payload is empty.", nameof(encoded));
        }

        using var data = SKData.CreateCopy(encoded);
        using var codec = SKCodec.Create(data)
            ?? throw new InvalidDataException("Tile payload is not a recognised raster image.");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        if (codec.GetPixels(info, bitmap.GetPixels()) != SKCodecResult.Success)
        {
            throw new InvalidDataException("Failed to decode tile pixels.");
        }
        return new DecodedRasterTile(info.Width, info.Height, bitmap.Bytes);
    }
}