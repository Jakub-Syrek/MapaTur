using System.Buffers.Binary;
using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Partitions one unified hybrid surface into 32/64/128 m RMP3 pages and packs its GPU vertex layout.
/// A source triangle is assigned to exactly one page, so partitioning cannot recreate the covered DEM.
/// </summary>
public static class HybridTerrainPageBaker
{
    private const float MinimumExtentMeters = 0.001f;

    public static IReadOnlyList<HybridTerrainMeshPage> Bake(
        HybridTerrainMesh mesh,
        float pageSizeMeters,
        byte lod,
        float geometricError)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (!float.IsFinite(pageSizeMeters) || pageSizeMeters <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSizeMeters));
        }

        var trianglesByPage = new Dictionary<(int X, int Y), List<(uint A, uint B, uint C)>>();
        for (int i = 0; i < mesh.Indices.Length; i += 3)
        {
            uint a = mesh.Indices[i];
            uint b = mesh.Indices[i + 1];
            uint c = mesh.Indices[i + 2];
            Vector3 centroid = (
                mesh.Positions[checked((int)a)]
                + mesh.Positions[checked((int)b)]
                + mesh.Positions[checked((int)c)]) / 3f;
            var key = (
                X: (int)MathF.Floor(centroid.X / pageSizeMeters),
                Y: (int)MathF.Floor(centroid.Y / pageSizeMeters));
            if (!trianglesByPage.TryGetValue(key, out List<(uint A, uint B, uint C)>? triangles))
            {
                triangles = [];
                trianglesByPage.Add(key, triangles);
            }

            triangles.Add((a, b, c));
        }

        return trianglesByPage
            .OrderBy(entry => entry.Key.X)
            .ThenBy(entry => entry.Key.Y)
            .Select(entry => BuildPage(
                mesh,
                entry.Value,
                lod,
                entry.Key.X,
                entry.Key.Y,
                geometricError))
            .ToArray();
    }

    private static HybridTerrainMeshPage BuildPage(
        HybridTerrainMesh source,
        IReadOnlyList<(uint A, uint B, uint C)> triangles,
        byte lod,
        int pageX,
        int pageY,
        float geometricError)
    {
        var sourceIndices = new List<uint>();
        var localIndexBySource = new Dictionary<uint, ushort>();
        var indices = new ushort[checked(triangles.Count * 3)];
        int destinationIndex = 0;
        foreach ((uint a, uint b, uint c) in triangles)
        {
            indices[destinationIndex++] = GetOrAdd(a, sourceIndices, localIndexBySource, pageX, pageY);
            indices[destinationIndex++] = GetOrAdd(b, sourceIndices, localIndexBySource, pageX, pageY);
            indices[destinationIndex++] = GetOrAdd(c, sourceIndices, localIndexBySource, pageX, pageY);
        }

        Vector3 minimum = sourceIndices
            .Select(index => source.Positions[checked((int)index)])
            .Aggregate(Vector3.Min);
        Vector3 maximum = sourceIndices
            .Select(index => source.Positions[checked((int)index)])
            .Aggregate(Vector3.Max);
        Vector3 extent = Vector3.Max(maximum - minimum, new Vector3(MinimumExtentMeters));
        Vector3 halfQuantizationStep = extent / (2f * ushort.MaxValue);
        float reportedError = Math.Max(geometricError, halfQuantizationStep.Length());
        var vertices = new byte[checked(sourceIndices.Count * HybridTerrainMeshPage.VertexStrideBytes)];
        for (int i = 0; i < sourceIndices.Count; i++)
        {
            int sourceIndex = checked((int)sourceIndices[i]);
            PackVertex(
                vertices.AsSpan(
                    i * HybridTerrainMeshPage.VertexStrideBytes,
                    HybridTerrainMeshPage.VertexStrideBytes),
                source.Positions[sourceIndex],
                source.Normals[sourceIndex],
                source.OrthoUvs[sourceIndex],
                source.AmbientOcclusion[sourceIndex],
                source.RockBlend[sourceIndex],
                source.MaterialVariants[sourceIndex],
                minimum,
                extent);
        }

        return new HybridTerrainMeshPage(
            lod,
            pageX,
            pageY,
            minimum,
            extent,
            reportedError,
            vertices,
            indices);
    }

    private static ushort GetOrAdd(
        uint sourceIndex,
        ICollection<uint> sourceIndices,
        IDictionary<uint, ushort> localIndexBySource,
        int pageX,
        int pageY)
    {
        if (localIndexBySource.TryGetValue(sourceIndex, out ushort existing))
        {
            return existing;
        }

        if (sourceIndices.Count >= HybridTerrainMeshPage.MaxVertices)
        {
            throw new InvalidOperationException(
                $"Hybrid terrain page ({pageX},{pageY}) exceeds {HybridTerrainMeshPage.MaxVertices} vertices; "
                + "use a smaller leaf page or simplify its offline LOD.");
        }

        ushort local = checked((ushort)sourceIndices.Count);
        sourceIndices.Add(sourceIndex);
        localIndexBySource.Add(sourceIndex, local);
        return local;
    }

    private static void PackVertex(
        Span<byte> destination,
        Vector3 position,
        Vector3 normal,
        Vector2 orthoUv,
        byte ambientOcclusion,
        byte rockBlend,
        ushort materialVariant,
        Vector3 minimum,
        Vector3 extent)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(destination, QuantizeUnorm(position.X, minimum.X, extent.X));
        BinaryPrimitives.WriteUInt16LittleEndian(destination[2..], QuantizeUnorm(position.Y, minimum.Y, extent.Y));
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], QuantizeUnorm(position.Z, minimum.Z, extent.Z));
        Vector2 octahedral = EncodeOctahedral(normal);
        BinaryPrimitives.WriteInt16LittleEndian(destination[6..], PackSnorm(octahedral.X));
        BinaryPrimitives.WriteInt16LittleEndian(destination[8..], PackSnorm(octahedral.Y));
        BinaryPrimitives.WriteUInt16LittleEndian(destination[10..], QuantizeUnorm(orthoUv.X, 0f, 1f));
        BinaryPrimitives.WriteUInt16LittleEndian(destination[12..], QuantizeUnorm(orthoUv.Y, 0f, 1f));
        destination[14] = ambientOcclusion;
        destination[15] = rockBlend;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[16..], materialVariant);
        destination[18] = 0;
        destination[19] = 0;
    }

    private static ushort QuantizeUnorm(float value, float minimum, float extent)
    {
        float normalized = Math.Clamp((value - minimum) / extent, 0f, 1f);
        return (ushort)MathF.Round(normalized * ushort.MaxValue);
    }

    private static Vector2 EncodeOctahedral(Vector3 normal)
    {
        normal = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitZ;
        float inverseL1 = 1f / (MathF.Abs(normal.X) + MathF.Abs(normal.Y) + MathF.Abs(normal.Z));
        var encoded = new Vector2(normal.X * inverseL1, normal.Y * inverseL1);
        if (normal.Z < 0f)
        {
            float oldX = encoded.X;
            encoded.X = (1f - MathF.Abs(encoded.Y)) * MathF.CopySign(1f, oldX);
            encoded.Y = (1f - MathF.Abs(oldX)) * MathF.CopySign(1f, encoded.Y);
        }

        return encoded;
    }

    private static short PackSnorm(float value) =>
        (short)MathF.Round(Math.Clamp(value, -1f, 1f) * short.MaxValue);
}
