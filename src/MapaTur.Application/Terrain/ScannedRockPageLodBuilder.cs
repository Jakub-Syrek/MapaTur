using System.Buffers.Binary;
using System.Numerics;

namespace MapaTur.Application.Terrain;

public readonly record struct ScannedRockSimplificationRequest(
    int TargetIndexCount,
    float MaximumGeometricErrorMeters,
    bool LockBorder,
    bool ErrorIsAbsolute = true);

public readonly record struct ScannedRockIndexSimplification(
    uint[] Indices,
    float GeometricErrorMeters);

/// <summary>
/// Offline-only simplifier contract. Implementations return indices into the original packed vertex set:
/// retained positions, normals, UVs and material weights therefore remain bit-identical to LOD0.
/// </summary>
public interface IScannedRockIndexSimplifier
{
    ScannedRockIndexSimplification Simplify(
        ReadOnlySpan<uint> indices,
        ReadOnlySpan<float> positions,
        int vertexCount,
        ScannedRockSimplificationRequest request);
}

/// <summary>
/// Builds same-cell RMP2 LODs without modifying the accepted V91 surface. LOD0 keeps its exact payload,
/// while LOD1/2 only remove triangles and unused original vertices. Topological page borders are locked by
/// contract so independently reduced neighbouring pages do not develop cracks.
/// </summary>
public static class ScannedRockPageLodBuilder
{
    public static ScannedRockMeshPage CreateFinestCopy(ScannedRockMeshPage source)
    {
        ValidateFinestSource(source);
        return new ScannedRockMeshPage(
            lod: 0,
            source.PageX,
            source.PageY,
            source.WorldMin,
            source.WorldExtent,
            QuantizationErrorMeters(source.WorldExtent),
            source.MaterialPageId,
            source.VertexData,
            source.Indices);
    }

    public static ScannedRockMeshPage Build(
        ScannedRockMeshPage source,
        byte lod,
        float targetTriangleFraction,
        float maximumGeometricErrorMeters,
        IScannedRockIndexSimplifier simplifier)
    {
        ValidateFinestSource(source);
        ArgumentNullException.ThrowIfNull(simplifier);
        if (lod is < 1 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(lod));
        }

        if (!float.IsFinite(targetTriangleFraction)
            || targetTriangleFraction <= 0f
            || targetTriangleFraction >= 1f)
        {
            throw new ArgumentOutOfRangeException(nameof(targetTriangleFraction));
        }

        if (!float.IsFinite(maximumGeometricErrorMeters) || maximumGeometricErrorMeters <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumGeometricErrorMeters));
        }

        uint[] sourceIndices = Array.ConvertAll(source.Indices, index => (uint)index);
        float[] positions = DecodePositions(source);
        int sourceTriangleCount = source.IndexCount / 3;
        int targetTriangleCount = Math.Clamp(
            (int)MathF.Ceiling(sourceTriangleCount * targetTriangleFraction),
            1,
            sourceTriangleCount);
        var request = new ScannedRockSimplificationRequest(
            targetTriangleCount * 3,
            maximumGeometricErrorMeters,
            LockBorder: true);
        ScannedRockIndexSimplification simplified = simplifier.Simplify(
            sourceIndices,
            positions,
            source.VertexCount,
            request);
        ValidateResult(source, simplified, maximumGeometricErrorMeters);

        (byte[] vertexData, ushort[] indices) = CompactRetainedVertices(
            source.VertexData,
            source.VertexCount,
            simplified.Indices);
        float totalError = checked(
            QuantizationErrorMeters(source.WorldExtent) + simplified.GeometricErrorMeters);
        return new ScannedRockMeshPage(
            lod,
            source.PageX,
            source.PageY,
            source.WorldMin,
            source.WorldExtent,
            totalError,
            source.MaterialPageId,
            vertexData,
            indices);
    }

    private static void ValidateFinestSource(ScannedRockMeshPage source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Lod != 0)
        {
            throw new ArgumentException("LOD1/2 must be derived directly from the accepted LOD0 page.", nameof(source));
        }
    }

    private static void ValidateResult(
        ScannedRockMeshPage source,
        ScannedRockIndexSimplification result,
        float maximumGeometricErrorMeters)
    {
        ArgumentNullException.ThrowIfNull(result.Indices);
        if (result.Indices.Length == 0
            || result.Indices.Length % 3 != 0
            || result.Indices.Length > source.IndexCount
            || result.Indices.Any(index => index >= source.VertexCount))
        {
            throw new InvalidDataException("Simplifier returned an invalid triangle list.");
        }

        if (!float.IsFinite(result.GeometricErrorMeters)
            || result.GeometricErrorMeters < 0f
            || result.GeometricErrorMeters > maximumGeometricErrorMeters + 1e-5f)
        {
            throw new InvalidDataException(
                $"Simplifier exceeded the {maximumGeometricErrorMeters:F3} m absolute error budget.");
        }
    }

    private static float[] DecodePositions(ScannedRockMeshPage source)
    {
        var positions = new float[source.VertexCount * 3];
        for (int index = 0; index < source.VertexCount; index++)
        {
            ReadOnlySpan<byte> vertex = source.VertexData.AsSpan(
                index * ScannedRockMeshPage.VertexStrideBytes,
                ScannedRockMeshPage.VertexStrideBytes);
            positions[(index * 3) + 0] = DecodeUnorm(vertex, source.WorldMin.X, source.WorldExtent.X);
            positions[(index * 3) + 1] = DecodeUnorm(vertex[2..], source.WorldMin.Y, source.WorldExtent.Y);
            positions[(index * 3) + 2] = DecodeUnorm(vertex[4..], source.WorldMin.Z, source.WorldExtent.Z);
        }

        return positions;
    }

    private static (byte[] VertexData, ushort[] Indices) CompactRetainedVertices(
        byte[] sourceVertexData,
        int sourceVertexCount,
        IReadOnlyList<uint> sourceIndices)
    {
        var oldToNew = new int[sourceVertexCount];
        Array.Fill(oldToNew, -1);
        var retainedVertices = new List<int>();
        var compactIndices = new ushort[sourceIndices.Count];
        for (int index = 0; index < sourceIndices.Count; index++)
        {
            int oldVertex = checked((int)sourceIndices[index]);
            int compactVertex = oldToNew[oldVertex];
            if (compactVertex < 0)
            {
                compactVertex = retainedVertices.Count;
                if (compactVertex > ushort.MaxValue)
                {
                    throw new InvalidDataException("Simplified RMP2 page exceeds the ushort vertex limit.");
                }

                oldToNew[oldVertex] = compactVertex;
                retainedVertices.Add(oldVertex);
            }

            compactIndices[index] = (ushort)compactVertex;
        }

        var compactVertexData =
            new byte[retainedVertices.Count * ScannedRockMeshPage.VertexStrideBytes];
        for (int compact = 0; compact < retainedVertices.Count; compact++)
        {
            Buffer.BlockCopy(
                sourceVertexData,
                retainedVertices[compact] * ScannedRockMeshPage.VertexStrideBytes,
                compactVertexData,
                compact * ScannedRockMeshPage.VertexStrideBytes,
                ScannedRockMeshPage.VertexStrideBytes);
        }

        return (compactVertexData, compactIndices);
    }

    private static float DecodeUnorm(ReadOnlySpan<byte> source, float minimum, float extent) =>
        minimum + ((BinaryPrimitives.ReadUInt16LittleEndian(source) / (float)ushort.MaxValue) * extent);

    private static float QuantizationErrorMeters(Vector3 extent)
    {
        Vector3 halfStep = extent / (ushort.MaxValue * 2f);
        return halfStep.Length();
    }
}
