namespace MapaTur.Application.Terrain;

/// <summary>
/// Removes capture-specific warm colour casts after multi-scan synthesis while preserving local luminance
/// structure. The result targets dark, weakly coloured high-mountain rock rather than brown desert stone.
/// </summary>
public static class RockAlbedoNeutralizer
{
    private const float ColourRetention = 0.08f;
    private const float NeutralBrightness = 0.90f;
    private const float PhotographicContrast = 0.22f;

    public static byte[] Neutralize(byte[] rgba)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        if (rgba.Length == 0 || rgba.Length % 4 != 0)
        {
            throw new ArgumentException("Rock albedo must contain complete RGBA pixels.", nameof(rgba));
        }

        byte[] result = rgba.ToArray();
        float meanLuminance = Enumerable.Range(0, rgba.Length / 4)
            .Where(index => rgba[(index * 4) + 3] != 0)
            .Select(index => Luminance(rgba, index * 4))
            .DefaultIfEmpty(128f)
            .Average();
        for (int pixel = 0; pixel < result.Length; pixel += 4)
        {
            float luminance = Luminance(rgba, pixel);
            float compressed = meanLuminance + ((luminance - meanLuminance) * PhotographicContrast);
            float neutral = compressed * NeutralBrightness;
            result[pixel] = Blend(rgba[pixel], neutral);
            result[pixel + 1] = Blend(rgba[pixel + 1], neutral);
            result[pixel + 2] = Blend(rgba[pixel + 2], neutral);
        }

        return result;
    }

    private static float Luminance(byte[] rgba, int pixel) =>
        (0.2126f * rgba[pixel])
        + (0.7152f * rgba[pixel + 1])
        + (0.0722f * rgba[pixel + 2]);

    private static byte Blend(byte source, float neutral)
    {
        float value = (source * ColourRetention) + (neutral * (1f - ColourRetention));
        return (byte)Math.Clamp(MathF.Round(value), byte.MinValue, byte.MaxValue);
    }
}
