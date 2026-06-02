namespace MapaTur.Application.Terrain;

/// <summary>
/// Decodes Terrarium-encoded elevation tiles (e.g. the AWS "elevation-tiles-prod/terrarium" set) into
/// elevations in metres. Terrarium packs height into RGB as
/// <c>elevation = (R · 256 + G + B / 256) − 32768</c>, giving ~1/256 m precision over a ±32768 m range.
/// Used to stream terrain tiles online so the 3D view can cover any area, not just the bundled DEM.
/// </summary>
public static class TerrariumDecoder
{
    /// <summary>Decodes a single Terrarium RGB triple to metres.</summary>
    public static float DecodePixel(byte r, byte g, byte b)
        => ((r * 256f) + g + (b / 256f)) - 32768f;

    /// <summary>
    /// Decodes a tightly-packed top-row-first RGBA8 terrarium tile (<paramref name="width"/> ×
    /// <paramref name="height"/>) into a row-major elevation grid in metres.
    /// </summary>
    public static float[] Decode(byte[] rgba, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Dimensions must be positive.");
        }

        int count = width * height;
        if (rgba.Length < count * 4)
        {
            throw new ArgumentException("Pixel buffer is smaller than width × height × 4.", nameof(rgba));
        }

        var elevations = new float[count];
        for (int i = 0; i < count; i++)
        {
            int p = i * 4;
            elevations[i] = DecodePixel(rgba[p], rgba[p + 1], rgba[p + 2]);
        }

        return elevations;
    }
}