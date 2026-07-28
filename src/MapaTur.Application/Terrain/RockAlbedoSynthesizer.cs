namespace MapaTur.Application.Terrain;

/// <summary>One decoded scan albedo used by the offline non-periodic material synthesizer.</summary>
public sealed class RockAlbedoTile
{
    public RockAlbedoTile(int width, int height, byte[] rgba)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        ArgumentNullException.ThrowIfNull(rgba);
        if (rgba.Length != checked(width * height * 4))
        {
            throw new ArgumentException("Rock albedo must contain exactly one RGBA value per pixel.", nameof(rgba));
        }

        Width = width;
        Height = height;
        Rgba = rgba;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Rgba { get; }
}

/// <summary>
/// Produces one unique material page by smoothly mixing several scan albedos through domain-warped,
/// seeded fields. The source images wrap internally, but no output-axis translation has a fixed period.
/// </summary>
public static class RockAlbedoSynthesizer
{
    public static byte[] Synthesize(
        IReadOnlyList<RockAlbedoTile> sources,
        int outputWidth,
        int outputHeight,
        int seed)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count < 2)
        {
            throw new ArgumentException("Non-periodic rock synthesis needs at least two scan albedos.", nameof(sources));
        }

        if (outputWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(outputWidth));
        }

        if (outputHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(outputHeight));
        }

        IReadOnlyList<byte[]> harmonized = RockAlbedoHarmonizer.Harmonize(
            sources.Select(source => source.Rgba).ToArray());
        var prepared = sources
            .Select((source, index) => new RockAlbedoTile(source.Width, source.Height, harmonized[index]))
            .ToArray();
        var result = new byte[checked(outputWidth * outputHeight * 4)];
        var weights = new float[prepared.Length];
        Span<float> sampled = stackalloc float[4];
        float aspect = outputWidth / (float)outputHeight;

        for (int y = 0; y < outputHeight; y++)
        {
            float v = (y + 0.5f) / outputHeight;
            for (int x = 0; x < outputWidth; x++)
            {
                float u = ((x + 0.5f) / outputWidth) * aspect;
                float warpX = FractalNoise((u * 1.37f) + 13.1f, (v * 1.37f) - 7.8f, seed ^ 0x43b2, 3);
                float warpY = FractalNoise((u * 1.11f) - 5.4f, (v * 1.11f) + 21.7f, seed ^ 0x791e, 3);
                float warpedU = u + (warpX * 0.23f);
                float warpedV = v + (warpY * 0.23f);

                float weightSum = 0f;
                for (int sourceIndex = 0; sourceIndex < prepared.Length; sourceIndex++)
                {
                    uint sourceSeed = Mix(unchecked((uint)seed) + ((uint)sourceIndex * 0x9e3779b9u));
                    float offsetX = ((sourceSeed & 0xffffu) / 65535f) * 31f;
                    float offsetY = (((sourceSeed >> 16) & 0xffffu) / 65535f) * 31f;
                    float seedRotation = (Mix(unchecked((uint)seed)) & 0xffffu) / 65535f;
                    float anchorAngle = MathF.Tau * ((sourceIndex / (float)prepared.Length) + seedRotation);
                    float anchorRadius = 0.27f
                        + ((((sourceSeed >> 9) & 0xffu) / 255f) * 0.08f);
                    float anchorU = 0.5f + (MathF.Cos(anchorAngle) * anchorRadius);
                    float anchorV = 0.5f + (MathF.Sin(anchorAngle) * anchorRadius);
                    float distanceToAnchor = MathF.Sqrt(
                        MathF.Pow((u / aspect) - anchorU, 2f)
                        + MathF.Pow(v - anchorV, 2f));
                    float anchorBias = Math.Clamp(1f - (distanceToAnchor * 2.35f), 0f, 1f) * 0.8f;
                    float field = FractalNoise(
                        (warpedU * 1.63f) + offsetX,
                        (warpedV * 1.63f) + offsetY,
                        unchecked((int)sourceSeed),
                        3) + anchorBias;
                    float weight = MathF.Pow(MathF.Max(0.035f, field + 1.05f), 5.5f);
                    weights[sourceIndex] = weight;
                    weightSum += weight;
                }

                sampled.Clear();
                for (int sourceIndex = 0; sourceIndex < prepared.Length; sourceIndex++)
                {
                    float weight = weights[sourceIndex] / weightSum;
                    SampleWarped(
                        prepared[sourceIndex],
                        warpedU,
                        warpedV,
                        seed,
                        sourceIndex,
                        sampled,
                        weight);
                }

                int destination = ((y * outputWidth) + x) * 4;
                result[destination] = ToByte(sampled[0]);
                result[destination + 1] = ToByte(sampled[1]);
                result[destination + 2] = ToByte(sampled[2]);
                result[destination + 3] = ToByte(sampled[3]);
            }
        }

        return result;
    }

    private static void SampleWarped(
        RockAlbedoTile source,
        float u,
        float v,
        int seed,
        int sourceIndex,
        Span<float> destination,
        float weight)
    {
        uint transformHash = Mix(unchecked((uint)seed) ^ ((uint)(sourceIndex + 1) * 0x85ebca6bu));
        float angle = ((transformHash & 0xffffu) / 65535f) * MathF.Tau;
        float scale = 1.13f + ((((transformHash >> 16) & 0xffffu) / 65535f) * 1.31f);
        float cosine = MathF.Cos(angle);
        float sine = MathF.Sin(angle);
        float rotatedU = ((u * cosine) - (v * sine)) * scale;
        float rotatedV = ((u * sine) + (v * cosine)) * scale;

        float fineWarpU = FractalNoise(
            (u * 4.71f) + (sourceIndex * 6.17f),
            (v * 4.71f) - (sourceIndex * 3.91f),
            seed ^ (sourceIndex * 7919),
            2);
        float fineWarpV = FractalNoise(
            (u * 5.23f) - (sourceIndex * 2.47f),
            (v * 5.23f) + (sourceIndex * 7.13f),
            seed ^ (sourceIndex * 104729) ^ 0x51f2,
            2);
        float sampleU = rotatedU + (fineWarpU * 0.14f) + ((transformHash & 0xffu) / 255f);
        float sampleV = rotatedV + (fineWarpV * 0.14f) + (((transformHash >> 8) & 0xffu) / 255f);
        BilinearSample(source, sampleU, sampleV, destination, weight);
    }

    private static void BilinearSample(
        RockAlbedoTile source,
        float u,
        float v,
        Span<float> destination,
        float weight)
    {
        float pixelX = PositiveFraction(u) * source.Width;
        float pixelY = PositiveFraction(v) * source.Height;
        int x0 = FloorMod((int)MathF.Floor(pixelX), source.Width);
        int y0 = FloorMod((int)MathF.Floor(pixelY), source.Height);
        int x1 = (x0 + 1) % source.Width;
        int y1 = (y0 + 1) % source.Height;
        float tx = pixelX - MathF.Floor(pixelX);
        float ty = pixelY - MathF.Floor(pixelY);

        for (int channel = 0; channel < 4; channel++)
        {
            float top = Lerp(
                source.Rgba[((y0 * source.Width) + x0) * 4 + channel],
                source.Rgba[((y0 * source.Width) + x1) * 4 + channel],
                tx);
            float bottom = Lerp(
                source.Rgba[((y1 * source.Width) + x0) * 4 + channel],
                source.Rgba[((y1 * source.Width) + x1) * 4 + channel],
                tx);
            destination[channel] += Lerp(top, bottom, ty) * weight;
        }
    }

    private static float FractalNoise(float x, float y, int seed, int octaves)
    {
        float value = 0f;
        float amplitude = 0.5714286f;
        for (int octave = 0; octave < octaves; octave++)
        {
            value += ValueNoise(x, y, seed + (octave * 1013)) * amplitude;
            x = (x * 2.07f) + 1.37f;
            y = (y * 2.03f) - 2.11f;
            amplitude *= 0.5f;
        }

        return value;
    }

    private static float ValueNoise(float x, float y, int seed)
    {
        int x0 = (int)MathF.Floor(x);
        int y0 = (int)MathF.Floor(y);
        float tx = Smooth(x - x0);
        float ty = Smooth(y - y0);
        float a = HashSigned(x0, y0, seed);
        float b = HashSigned(x0 + 1, y0, seed);
        float c = HashSigned(x0, y0 + 1, seed);
        float d = HashSigned(x0 + 1, y0 + 1, seed);
        return Lerp(Lerp(a, b, tx), Lerp(c, d, tx), ty);
    }

    private static float HashSigned(int x, int y, int seed)
    {
        uint hash = unchecked((uint)seed);
        hash = Mix(hash ^ unchecked((uint)x * 0x8da6b343u));
        hash = Mix(hash ^ unchecked((uint)y * 0xd8163841u));
        return ((hash & 0x00ffffffu) / 8_388_607.5f) - 1f;
    }

    private static uint Mix(uint value)
    {
        value ^= value >> 16;
        value *= 0x7feb352du;
        value ^= value >> 15;
        value *= 0x846ca68bu;
        value ^= value >> 16;
        return value;
    }

    private static float Smooth(float value) => value * value * (3f - (2f * value));
    private static float Lerp(float a, float b, float amount) => a + ((b - a) * amount);
    private static float PositiveFraction(float value) => value - MathF.Floor(value);
    private static int FloorMod(int value, int modulus) => ((value % modulus) + modulus) % modulus;
    private static byte ToByte(float value) => (byte)Math.Clamp(MathF.Round(value), byte.MinValue, byte.MaxValue);
}
