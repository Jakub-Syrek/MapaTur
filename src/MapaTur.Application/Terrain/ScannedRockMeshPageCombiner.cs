using System.Buffers.Binary;
using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Joins independently baked region chunks that occupy the same RMP2 streaming key. Position quantization is
/// rebuilt against the combined bounds; normals, UVs, seam weights and material attributes remain GPU-ready.
/// </summary>
public static class ScannedRockMeshPageCombiner
{
    private const float MinimumExtentMeters = 0.001f;

    public static ScannedRockMeshPage Combine(
        ScannedRockMeshPage first,
        ScannedRockMeshPage second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        if (first.Lod != second.Lod
            || first.PageX != second.PageX
            || first.PageY != second.PageY
            || first.MaterialPageId != second.MaterialPageId)
        {
            throw new ArgumentException("Only chunks with the same RMP2 key and material can be combined.");
        }

        int vertexCount = checked(first.VertexCount + second.VertexCount);
        if (vertexCount > ScannedRockMeshPage.MaxVertices)
        {
            throw new InvalidOperationException(
                $"Combined RMP2 page ({first.PageX},{first.PageY}) exceeds "
                + $"{ScannedRockMeshPage.MaxVertices} vertices.");
        }

        Vector3[] positions = DecodePositions(first)
            .Concat(DecodePositions(second))
            .ToArray();
        Vector3 minimum = positions.Aggregate(Vector3.Min);
        Vector3 maximum = positions.Aggregate(Vector3.Max);
        Vector3 extent = Vector3.Max(maximum - minimum, new Vector3(MinimumExtentMeters));
        var vertexData = new byte[checked(vertexCount * ScannedRockMeshPage.VertexStrideBytes)];
        CopyVertices(first, positions, sourcePositionOffset: 0, vertexData, minimum, extent);
        CopyVertices(second, positions, sourcePositionOffset: first.VertexCount, vertexData, minimum, extent);

        var indices = new ushort[checked(first.IndexCount + second.IndexCount)];
        first.Indices.CopyTo(indices, 0);
        for (int index = 0; index < second.IndexCount; index++)
        {
            indices[first.IndexCount + index] = checked((ushort)(second.Indices[index] + first.VertexCount));
        }

        return new ScannedRockMeshPage(
            first.Lod,
            first.PageX,
            first.PageY,
            minimum,
            extent,
            MathF.Max(first.GeometricError, second.GeometricError),
            first.MaterialPageId,
            vertexData,
            indices);
    }

    private static IEnumerable<Vector3> DecodePositions(ScannedRockMeshPage page)
    {
        for (int index = 0; index < page.VertexCount; index++)
        {
            ReadOnlySpan<byte> source = page.VertexData.AsSpan(
                index * ScannedRockMeshPage.VertexStrideBytes,
                ScannedRockMeshPage.VertexStrideBytes);
            yield return new Vector3(
                DecodeUnorm(source, page.WorldMin.X, page.WorldExtent.X),
                DecodeUnorm(source[2..], page.WorldMin.Y, page.WorldExtent.Y),
                DecodeUnorm(source[4..], page.WorldMin.Z, page.WorldExtent.Z));
        }
    }

    private static void CopyVertices(
        ScannedRockMeshPage sourcePage,
        IReadOnlyList<Vector3> positions,
        int sourcePositionOffset,
        byte[] destinationData,
        Vector3 minimum,
        Vector3 extent)
    {
        int destinationVertexOffset = sourcePositionOffset;
        for (int index = 0; index < sourcePage.VertexCount; index++)
        {
            ReadOnlySpan<byte> source = sourcePage.VertexData.AsSpan(
                index * ScannedRockMeshPage.VertexStrideBytes,
                ScannedRockMeshPage.VertexStrideBytes);
            Span<byte> destination = destinationData.AsSpan(
                (destinationVertexOffset + index) * ScannedRockMeshPage.VertexStrideBytes,
                ScannedRockMeshPage.VertexStrideBytes);
            Vector3 position = positions[sourcePositionOffset + index];
            BinaryPrimitives.WriteUInt16LittleEndian(
                destination,
                EncodeUnorm(position.X, minimum.X, extent.X));
            BinaryPrimitives.WriteUInt16LittleEndian(
                destination[2..],
                EncodeUnorm(position.Y, minimum.Y, extent.Y));
            BinaryPrimitives.WriteUInt16LittleEndian(
                destination[4..],
                EncodeUnorm(position.Z, minimum.Z, extent.Z));
            source[6..].CopyTo(destination[6..]);
        }
    }

    private static float DecodeUnorm(ReadOnlySpan<byte> source, float minimum, float extent) =>
        minimum + ((BinaryPrimitives.ReadUInt16LittleEndian(source) / (float)ushort.MaxValue) * extent);

    private static ushort EncodeUnorm(float value, float minimum, float extent) =>
        (ushort)MathF.Round(Math.Clamp((value - minimum) / extent, 0f, 1f) * ushort.MaxValue);
}
