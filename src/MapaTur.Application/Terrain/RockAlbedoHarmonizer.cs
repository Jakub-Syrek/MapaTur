namespace MapaTur.Application.Terrain;

/// <summary>
/// Removes exposure differences between independently captured photogrammetry scans before they enter one
/// atlas. It applies one luminance offset per scan, preserving all local contrast, colour variation and alpha.
/// </summary>
public static class RockAlbedoHarmonizer
{
    public static IReadOnlyList<byte[]> Harmonize(IReadOnlyList<byte[]> rgbaTiles)
    {
        ArgumentNullException.ThrowIfNull(rgbaTiles);
        if (rgbaTiles.Count == 0
            || rgbaTiles.Any(tile => tile is null || tile.Length == 0 || tile.Length % 4 != 0))
        {
            throw new ArgumentException("Albedo tiles must contain complete RGBA pixels.", nameof(rgbaTiles));
        }

        double[] means = rgbaTiles.Select(MeanLuminance).Order().ToArray();
        double target = means.Length % 2 == 1
            ? means[means.Length / 2]
            : (means[(means.Length / 2) - 1] + means[means.Length / 2]) * 0.5;
        var result = new byte[rgbaTiles.Count][];
        for (int tileIndex = 0; tileIndex < rgbaTiles.Count; tileIndex++)
        {
            byte[] source = rgbaTiles[tileIndex];
            byte[] destination = source.ToArray();
            double offset = target - MeanLuminance(source);
            for (int pixel = 0; pixel < destination.Length; pixel += 4)
            {
                destination[pixel] = Shift(source[pixel], offset);
                destination[pixel + 1] = Shift(source[pixel + 1], offset);
                destination[pixel + 2] = Shift(source[pixel + 2], offset);
            }

            result[tileIndex] = destination;
        }

        return result;
    }

    private static double MeanLuminance(byte[] rgba)
    {
        double sum = 0;
        int count = 0;
        for (int pixel = 0; pixel < rgba.Length; pixel += 4)
        {
            if (rgba[pixel + 3] == 0)
            {
                continue;
            }

            sum += (0.2126 * rgba[pixel])
                + (0.7152 * rgba[pixel + 1])
                + (0.0722 * rgba[pixel + 2]);
            count++;
        }

        if (count == 0)
        {
            throw new ArgumentException("Albedo tile contains no visible pixels.", nameof(rgba));
        }

        return sum / count;
    }

    private static byte Shift(byte value, double offset) =>
        (byte)Math.Clamp(Math.Round(value + offset), byte.MinValue, byte.MaxValue);
}
