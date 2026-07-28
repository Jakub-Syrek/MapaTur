using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Broad, smooth modulation for scanned relief. It changes the strength of existing scan geometry
/// without mixing phases, adding micro-facets, or introducing a repeating cell boundary.
/// </summary>
public static class RockReliefMacroEnvelope
{
    private const float MinimumStrength = 0.45f;

    public static float GetStrength(Vector3 worldPosition, float regionSizeMeters, int seed)
    {
        if (!IsFinite(worldPosition))
        {
            throw new ArgumentOutOfRangeException(nameof(worldPosition));
        }

        if (!float.IsFinite(regionSizeMeters) || regionSizeMeters <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(regionSizeMeters));
        }

        Vector3 position = worldPosition / regionSizeMeters;
        float noise = FractalNoise(position, unchecked((uint)seed));
        float normalized = Math.Clamp(0.5f + (noise * 0.5f), 0f, 1f);
        float shaped = normalized * normalized * (3f - (2f * normalized));
        return MinimumStrength + ((1f - MinimumStrength) * shaped);
    }

    private static float FractalNoise(Vector3 position, uint seed)
    {
        float value = 0f;
        float amplitude = 0.5714286f;
        for (int octave = 0; octave < 3; octave++)
        {
            value += ValueNoise(position, seed + ((uint)octave * 0x9E3779B9u)) * amplitude;
            position = new Vector3(
                (position.X * 2.03f) - (position.Y * 0.13f) + (position.Z * 0.07f) + 1.31f,
                (position.X * 0.11f) + (position.Y * 2.07f) - (position.Z * 0.17f) - 2.17f,
                (position.X * -0.09f) + (position.Y * 0.19f) + (position.Z * 1.97f) + 0.73f);
            amplitude *= 0.5f;
        }

        return value;
    }

    private static float ValueNoise(Vector3 position, uint seed)
    {
        int x0 = (int)MathF.Floor(position.X);
        int y0 = (int)MathF.Floor(position.Y);
        int z0 = (int)MathF.Floor(position.Z);
        float tx = Smooth(position.X - x0);
        float ty = Smooth(position.Y - y0);
        float tz = Smooth(position.Z - z0);

        float z0Top = Lerp(HashSigned(x0, y0, z0, seed), HashSigned(x0 + 1, y0, z0, seed), tx);
        float z0Bottom = Lerp(HashSigned(x0, y0 + 1, z0, seed), HashSigned(x0 + 1, y0 + 1, z0, seed), tx);
        float z1Top = Lerp(HashSigned(x0, y0, z0 + 1, seed), HashSigned(x0 + 1, y0, z0 + 1, seed), tx);
        float z1Bottom = Lerp(
            HashSigned(x0, y0 + 1, z0 + 1, seed),
            HashSigned(x0 + 1, y0 + 1, z0 + 1, seed),
            tx);
        return Lerp(Lerp(z0Top, z0Bottom, ty), Lerp(z1Top, z1Bottom, ty), tz);
    }

    private static float HashSigned(int x, int y, int z, uint seed)
    {
        uint hash = unchecked((uint)x) * 0x8DA6B343u;
        hash ^= unchecked((uint)y) * 0xD8163841u;
        hash ^= unchecked((uint)z) * 0xCB1AB31Fu;
        hash ^= seed;
        hash ^= hash >> 16;
        hash *= 0x7FEB352Du;
        hash ^= hash >> 15;
        hash *= 0x846CA68Bu;
        hash ^= hash >> 16;
        return ((hash & 0x00FFFFFFu) / 8_388_607.5f) - 1f;
    }

    private static float Smooth(float value) => value * value * (3f - (2f * value));
    private static float Lerp(float first, float second, float amount) =>
        first + ((second - first) * amount);
    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
