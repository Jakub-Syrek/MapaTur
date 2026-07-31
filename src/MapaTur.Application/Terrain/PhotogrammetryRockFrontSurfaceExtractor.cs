using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Removes untextured capture backs and near-edge-on technical closure faces while preserving every original
/// outward-facing photogrammetry triangle. It does not sample, rebuild or regularize the measured surface.
/// </summary>
public static class PhotogrammetryRockFrontSurfaceExtractor
{
    private const float MinimumFrontCosine = 0.18f;
    private const float MinimumTriangleShapeQuality = 0.012f;

    public static PhotogrammetryRockPrimitive Extract(
        PhotogrammetryRockPrimitive source,
        Vector3 frontDirection)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!IsFinite(frontDirection) || frontDirection.LengthSquared() < 0.25f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frontDirection),
                "Front direction must be finite and non-zero.");
        }

        Vector3 front = Vector3.Normalize(frontDirection);
        var indices = new List<uint>(source.Indices.Length);
        for (int offset = 0; offset < source.Indices.Length; offset += 3)
        {
            uint indexA = source.Indices[offset];
            uint indexB = source.Indices[offset + 1];
            uint indexC = source.Indices[offset + 2];
            Vector3 edgeA = source.Positions[indexB] - source.Positions[indexA];
            Vector3 edgeB = source.Positions[indexC] - source.Positions[indexA];
            Vector3 edgeC = source.Positions[indexC] - source.Positions[indexB];
            Vector3 faceNormal = Vector3.Cross(edgeA, edgeB);
            float normalLength = faceNormal.Length();
            float longestEdgeSquared = MathF.Max(
                edgeA.LengthSquared(),
                MathF.Max(edgeB.LengthSquared(), edgeC.LengthSquared()));
            if (normalLength <= 1e-10f
                || normalLength < longestEdgeSquared * MinimumTriangleShapeQuality
                || Vector3.Dot(faceNormal, front) < normalLength * MinimumFrontCosine)
            {
                continue;
            }

            indices.Add(indexA);
            indices.Add(indexB);
            indices.Add(indexC);
        }

        if (indices.Count == 0)
        {
            throw new InvalidDataException("The scan contains no outward-facing front triangles.");
        }

        return new PhotogrammetryRockPrimitive(
            source.Positions,
            source.Normals,
            source.TexCoords,
            indices.ToArray(),
            source.BaseColorImageBytes,
            source.SeamWeights);
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
