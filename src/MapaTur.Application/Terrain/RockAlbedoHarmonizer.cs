namespace MapaTur.Application.Terrain;

/// <summary>
/// Matches independently captured scans to the first, product-approved reference pattern before they enter
/// one atlas. Exposure and contrast are harmonized while local texture, colour differences and alpha remain.
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

        LuminanceStatistics target = CalculateStatistics(rgbaTiles[0]);
        var result = new byte[rgbaTiles.Count][];
        for (int tileIndex = 0; tileIndex < rgbaTiles.Count; tileIndex++)
        {
            byte[] source = rgbaTiles[tileIndex];
            byte[] destination = source.ToArray();
            LuminanceStatistics statistics = CalculateStatistics(source);
            double contrastGain = statistics.StandardDeviation > 0.5
                ? Math.Clamp(target.StandardDeviation / statistics.StandardDeviation, 0.4, 2.5)
                : 1.0;
            for (int pixel = 0; pixel < destination.Length; pixel += 4)
            {
                if (source[pixel + 3] == 0)
                {
                    continue;
                }

                destination[pixel] = Match(
                    source[pixel],
                    statistics.RedMean,
                    target.RedMean,
                    contrastGain);
                destination[pixel + 1] = Match(
                    source[pixel + 1],
                    statistics.GreenMean,
                    target.GreenMean,
                    contrastGain);
                destination[pixel + 2] = Match(
                    source[pixel + 2],
                    statistics.BlueMean,
                    target.BlueMean,
                    contrastGain);
            }

            result[tileIndex] = destination;
        }

        return result;
    }

    private static LuminanceStatistics CalculateStatistics(byte[] rgba)
    {
        double sum = 0;
        double squaredSum = 0;
        double redSum = 0;
        double greenSum = 0;
        double blueSum = 0;
        int count = 0;
        for (int pixel = 0; pixel < rgba.Length; pixel += 4)
        {
            if (rgba[pixel + 3] == 0)
            {
                continue;
            }

            double luminance = Luminance(rgba, pixel);
            sum += luminance;
            squaredSum += luminance * luminance;
            redSum += rgba[pixel];
            greenSum += rgba[pixel + 1];
            blueSum += rgba[pixel + 2];
            count++;
        }

        if (count == 0)
        {
            throw new ArgumentException("Albedo tile contains no visible pixels.", nameof(rgba));
        }

        double mean = sum / count;
        double variance = Math.Max(0.0, (squaredSum / count) - (mean * mean));
        return new LuminanceStatistics(
            mean,
            Math.Sqrt(variance),
            redSum / count,
            greenSum / count,
            blueSum / count);
    }

    private static double Luminance(byte[] rgba, int pixel) =>
        (0.2126 * rgba[pixel])
        + (0.7152 * rgba[pixel + 1])
        + (0.0722 * rgba[pixel + 2]);

    private static byte Match(byte value, double sourceMean, double targetMean, double contrastGain) =>
        (byte)Math.Clamp(
            Math.Round(targetMean + ((value - sourceMean) * contrastGain)),
            byte.MinValue,
            byte.MaxValue);

    private readonly record struct LuminanceStatistics(
        double Mean,
        double StandardDeviation,
        double RedMean,
        double GreenMean,
        double BlueMean);
}
