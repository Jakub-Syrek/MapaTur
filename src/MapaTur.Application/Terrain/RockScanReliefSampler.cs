using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>Linear, normalized height samples decoded from one real scanned-rock displacement map.</summary>
public sealed class RockHeightMap
{
    private readonly float[] samples;

    public RockHeightMap(int width, int height, IReadOnlyList<float> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (width < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        if (samples.Count != checked(width * height))
        {
            throw new ArgumentException("Rock height sample count does not match its dimensions.", nameof(samples));
        }

        if (samples.Any(value => !float.IsFinite(value) || value is < 0f or > 1f))
        {
            throw new ArgumentOutOfRangeException(nameof(samples), "Rock heights must be finite values in [0, 1].");
        }

        Width = width;
        Height = height;
        this.samples = samples.ToArray();
        Mean = this.samples.Average();
    }

    public int Width { get; }
    public int Height { get; }
    public float Mean { get; }

    public float SampleWrapped(float u, float v)
    {
        float x = Wrap01(u) * Width;
        float y = Wrap01(v) * Height;
        int x0 = (int)MathF.Floor(x) % Width;
        int y0 = (int)MathF.Floor(y) % Height;
        int x1 = (x0 + 1) % Width;
        int y1 = (y0 + 1) % Height;
        float tx = x - MathF.Floor(x);
        float ty = y - MathF.Floor(y);

        float top = Lerp(this[x0, y0], this[x1, y0], tx);
        float bottom = Lerp(this[x0, y1], this[x1, y1], tx);
        return Lerp(top, bottom, ty);
    }

    private float this[int x, int y] => samples[(y * Width) + x];
    private static float Wrap01(float value) => value - MathF.Floor(value);
    private static float Lerp(float a, float b, float t) => a + ((b - a) * t);
}

/// <summary>
/// Offline sampler for real scanned displacement. It uses four-corner stochastic texture synthesis and
/// variance restoration on three world projections, so no single scan period is stamped across a cliff.
/// </summary>
public sealed class RockScanReliefSampler
{
    private readonly RockHeightMap[] scans;
    private readonly float inverseFeatureSize;
    private readonly float amplitudeMeters;

    public RockScanReliefSampler(
        IReadOnlyList<RockHeightMap> scans,
        float featureSizeMeters,
        float amplitudeMeters)
    {
        ArgumentNullException.ThrowIfNull(scans);
        if (scans.Count == 0)
        {
            throw new ArgumentException("At least one scanned displacement map is required.", nameof(scans));
        }

        if (!float.IsFinite(featureSizeMeters) || featureSizeMeters <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(featureSizeMeters));
        }

        if (!float.IsFinite(amplitudeMeters) || amplitudeMeters <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(amplitudeMeters));
        }

        this.scans = scans.ToArray();
        inverseFeatureSize = 1f / featureSizeMeters;
        this.amplitudeMeters = amplitudeMeters;
    }

    public RockSurfaceSample Sample(Vector3 worldPosition, Vector3 surfaceNormal)
    {
        if (!IsFinite(worldPosition))
        {
            throw new ArgumentOutOfRangeException(nameof(worldPosition));
        }

        if (!IsFinite(surfaceNormal) || surfaceNormal.LengthSquared() < 1e-12f)
        {
            throw new ArgumentOutOfRangeException(nameof(surfaceNormal));
        }

        surfaceNormal = Vector3.Normalize(surfaceNormal);
        Vector3 weights = Vector3.Abs(surfaceNormal);
        weights *= weights;
        weights *= weights;
        weights /= weights.X + weights.Y + weights.Z;

        float xProjection = SampleProjection(
            Rotate(worldPosition.Y, worldPosition.Z, 0.371f),
            seed: 0xA511E9B3u);
        float yProjection = SampleProjection(
            Rotate(worldPosition.X, worldPosition.Z, -0.619f),
            seed: 0x63D83595u);
        float zProjection = SampleProjection(
            Rotate(worldPosition.X, worldPosition.Y, 0.917f),
            seed: 0xC2B2AE35u);

        float weightEnergy = MathF.Sqrt(
            (weights.X * weights.X) + (weights.Y * weights.Y) + (weights.Z * weights.Z));
        float centered = (
            (xProjection * weights.X)
            + (yProjection * weights.Y)
            + (zProjection * weights.Z)) / weightEnergy;
        float normalizedRelief = Math.Clamp(centered * 2f, -1f, 1f);
        float displacement = normalizedRelief * amplitudeMeters;
        byte ao = (byte)MathF.Round(Math.Clamp(0.68f + (0.32f * ((normalizedRelief + 1f) * 0.5f)), 0f, 1f) * 255f);
        ushort material = (ushort)(Hash(
            (int)MathF.Floor(worldPosition.X * 0.125f),
            (int)MathF.Floor(worldPosition.Y * 0.125f),
            (uint)MathF.Floor(worldPosition.Z * 0.125f)) & ushort.MaxValue);

        return new RockSurfaceSample(displacement, ao, material);
    }

    private float SampleProjection(Vector2 projectedMeters, uint seed)
    {
        Vector2 uv = projectedMeters * inverseFeatureSize;
        int x0 = (int)MathF.Floor(uv.X);
        int y0 = (int)MathF.Floor(uv.Y);
        float tx = Smooth(uv.X - x0);
        float ty = Smooth(uv.Y - y0);
        float w00 = (1f - tx) * (1f - ty);
        float w10 = tx * (1f - ty);
        float w01 = (1f - tx) * ty;
        float w11 = tx * ty;

        float centered =
            (w00 * SampleVariant(uv, x0, y0, seed))
            + (w10 * SampleVariant(uv, x0 + 1, y0, seed))
            + (w01 * SampleVariant(uv, x0, y0 + 1, seed))
            + (w11 * SampleVariant(uv, x0 + 1, y0 + 1, seed));
        float weightEnergy = MathF.Sqrt(
            (w00 * w00) + (w10 * w10) + (w01 * w01) + (w11 * w11));
        return centered / weightEnergy;
    }

    private float SampleVariant(Vector2 uv, int cellX, int cellY, uint seed)
    {
        uint hash = Hash(cellX, cellY, seed);
        RockHeightMap map = scans[(int)(hash % (uint)scans.Length)];
        int orientation = (int)((hash >> 8) & 7u);
        float offsetU = ((hash >> 12) & 1023u) / 1024f;
        float offsetV = ((hash >> 22) & 1023u) / 1024f;
        Vector2 transformed = Transform(uv, orientation);
        return map.SampleWrapped(transformed.X + offsetU, transformed.Y + offsetV) - map.Mean;
    }

    private static Vector2 Rotate(float u, float v, float angle)
    {
        float cosine = MathF.Cos(angle);
        float sine = MathF.Sin(angle);
        return new Vector2((u * cosine) - (v * sine), (u * sine) + (v * cosine));
    }

    private static Vector2 Transform(Vector2 value, int orientation) => orientation switch
    {
        0 => value,
        1 => new Vector2(-value.Y, value.X),
        2 => -value,
        3 => new Vector2(value.Y, -value.X),
        4 => new Vector2(-value.X, value.Y),
        5 => new Vector2(value.X, -value.Y),
        6 => new Vector2(value.Y, value.X),
        _ => new Vector2(-value.Y, -value.X),
    };

    private static float Smooth(float value) => value * value * (3f - (2f * value));

    private static uint Hash(int x, int y, uint seed)
    {
        unchecked
        {
            uint hash = (uint)x * 0x8DA6B343u;
            hash ^= (uint)y * 0xD8163841u;
            hash ^= seed;
            hash ^= hash >> 16;
            hash *= 0x7FEB352Du;
            hash ^= hash >> 15;
            hash *= 0x846CA68Bu;
            return hash ^ (hash >> 16);
        }
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
