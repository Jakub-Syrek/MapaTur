using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Creates deterministic, non-rigid variants of a complete photogrammetric scan. The deformation is
/// deliberately smooth and fades to zero at the scan frame, so neighbouring fitted surfaces can still
/// be welded to the terrain while the measured ledges, cracks and texture move together in the interior.
/// </summary>
public static class PhotogrammetryRockInternalWarper
{
    private const float TwoPi = 2f * MathF.PI;

    public static PhotogrammetryRockPrimitive Warp(
        PhotogrammetryRockPrimitive source,
        int seed)
    {
        ArgumentNullException.ThrowIfNull(source);

        float minX = source.Positions.Min(position => position.X);
        float maxX = source.Positions.Max(position => position.X);
        float minY = source.Positions.Min(position => position.Y);
        float maxY = source.Positions.Max(position => position.Y);
        float minZ = source.Positions.Min(position => position.Z);
        float maxZ = source.Positions.Max(position => position.Z);
        float width = maxX - minX;
        float height = maxY - minY;
        float depth = maxZ - minZ;
        if (width <= 1e-5f || height <= 1e-5f)
        {
            throw new ArgumentException(
                "The scan must have non-zero horizontal and vertical extent.",
                nameof(source));
        }

        float phaseA = SeedPhase(seed, 0x68bc21eb);
        float phaseB = SeedPhase(seed, 0x02e5be93);
        float phaseC = SeedPhase(seed, 0x7f4a7c15);
        float phaseD = SeedPhase(seed, unchecked((int)0x9e3779b9));
        var positions = new Vector3[source.Positions.Length];

        for (int index = 0; index < positions.Length; index++)
        {
            Vector3 position = source.Positions[index];
            float u = (position.X - minX) / width;
            float v = (position.Y - minY) / height;
            float frameDistance = MathF.Min(MathF.Min(u, 1f - u), MathF.Min(v, 1f - v));
            float envelope = SmoothStep(Math.Clamp(frameDistance / 0.14f, 0f, 1f));

            float horizontalField =
                (0.62f * MathF.Sin(TwoPi * ((v * 0.83f) + (u * 0.21f)) + phaseA))
                + (0.38f * MathF.Sin(TwoPi * ((v * 1.47f) - (u * 0.36f)) + phaseB));
            float verticalField =
                (0.58f * MathF.Sin(TwoPi * ((u * 0.71f) - (v * 0.18f)) + phaseC))
                + (0.42f * MathF.Sin(TwoPi * ((u * 1.31f) + (v * 0.43f)) + phaseA));
            float reliefField =
                (0.52f * MathF.Sin(TwoPi * ((u * 0.64f) + (v * 0.91f)) + phaseD))
                + (0.30f * MathF.Sin(TwoPi * ((u * 1.53f) - (v * 0.57f)) + phaseB))
                + (0.18f * MathF.Sin(TwoPi * ((u * 2.27f) + (v * 1.19f)) + phaseC));

            positions[index] = position + new Vector3(
                width * 0.045f * envelope * horizontalField,
                height * 0.038f * envelope * verticalField,
                depth * 0.16f * envelope * reliefField);
        }

        Vector3[] normals = RecalculateNormals(source, positions);
        return new PhotogrammetryRockPrimitive(
            positions,
            normals,
            source.TexCoords.ToArray(),
            source.Indices.ToArray(),
            source.BaseColorImageBytes,
            source.SeamWeights.ToArray());
    }

    private static Vector3[] RecalculateNormals(
        PhotogrammetryRockPrimitive source,
        Vector3[] positions)
    {
        var accumulated = new Vector3[positions.Length];
        for (int index = 0; index < source.Indices.Length; index += 3)
        {
            int a = (int)source.Indices[index];
            int b = (int)source.Indices[index + 1];
            int c = (int)source.Indices[index + 2];
            Vector3 faceNormal = Vector3.Cross(
                positions[b] - positions[a],
                positions[c] - positions[a]);
            accumulated[a] += faceNormal;
            accumulated[b] += faceNormal;
            accumulated[c] += faceNormal;
        }

        var normals = new Vector3[positions.Length];
        for (int index = 0; index < normals.Length; index++)
        {
            Vector3 candidate = accumulated[index];
            if (candidate.LengthSquared() <= 1e-12f)
            {
                normals[index] = source.Normals[index];
                continue;
            }

            candidate = Vector3.Normalize(candidate);
            normals[index] = Vector3.Dot(candidate, source.Normals[index]) < 0f
                ? -candidate
                : candidate;
        }

        return normals;
    }

    private static float SeedPhase(int seed, int salt)
    {
        uint value = unchecked((uint)(seed ^ salt));
        value ^= value >> 16;
        value *= 0x7feb352d;
        value ^= value >> 15;
        value *= 0x846ca68b;
        value ^= value >> 16;
        return (value / (float)uint.MaxValue) * TwoPi;
    }

    private static float SmoothStep(float value) =>
        value * value * (3f - (2f * value));
}
