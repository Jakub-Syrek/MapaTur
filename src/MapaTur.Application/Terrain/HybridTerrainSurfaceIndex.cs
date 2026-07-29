using System.Buffers.Binary;
using System.Numerics;

namespace MapaTur.Application.Terrain;

public readonly record struct HybridTerrainSurfaceQueryDiagnostics(
    int NodeTests,
    int TriangleTests);

/// <summary>
/// Immutable triangle BVH for repeated SampleHybridSurface calls. It is built once per resident hybrid
/// surface and prunes triangle groups whose AABB lies outside the query's bounded relief sphere.
/// </summary>
public sealed class HybridTerrainSurfaceIndex
{
    private const int LeafCapacity = 8;

    private readonly HybridTerrainMesh? mesh;
    private readonly HybridTerrainMeshPage? page;
    private readonly TriangleEntry[] entries;
    private readonly SpatialNode root;

    public HybridTerrainSurfaceIndex(HybridTerrainMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        this.mesh = mesh;
        entries = new TriangleEntry[mesh.TriangleCount];
        PopulateEntries();
        root = BuildNode(0, entries.Length);
    }

    public HybridTerrainSurfaceIndex(HybridTerrainMeshPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        this.page = page;
        entries = new TriangleEntry[page.IndexCount / 3];
        PopulateEntries();
        root = BuildNode(0, entries.Length);
    }

    internal int VertexIndexAt(int indexOffset) =>
        mesh is not null
            ? checked((int)mesh.Indices[indexOffset])
            : page!.Indices[indexOffset];

    internal Vector3 PositionAt(int vertexIndex)
    {
        if (mesh is not null)
        {
            return mesh.Positions[vertexIndex];
        }

        ReadOnlySpan<byte> source = page!.VertexData.AsSpan(
            vertexIndex * HybridTerrainMeshPage.VertexStrideBytes,
            HybridTerrainMeshPage.VertexStrideBytes);
        return new Vector3(
            DecodeUnorm(source, page.WorldMin.X, page.WorldExtent.X),
            DecodeUnorm(source[2..], page.WorldMin.Y, page.WorldExtent.Y),
            DecodeUnorm(source[4..], page.WorldMin.Z, page.WorldExtent.Z));
    }

    internal Vector3 NormalAt(int vertexIndex)
    {
        if (mesh is not null)
        {
            return mesh.Normals[vertexIndex];
        }

        ReadOnlySpan<byte> source = page!.VertexData.AsSpan(
            (vertexIndex * HybridTerrainMeshPage.VertexStrideBytes) + 6,
            4);
        float x = DecodeSnorm(BinaryPrimitives.ReadInt16LittleEndian(source));
        float y = DecodeSnorm(BinaryPrimitives.ReadInt16LittleEndian(source[2..]));
        float z = 1f - MathF.Abs(x) - MathF.Abs(y);
        if (z < 0f)
        {
            float oldX = x;
            x = (1f - MathF.Abs(y)) * MathF.CopySign(1f, oldX);
            y = (1f - MathF.Abs(oldX)) * MathF.CopySign(1f, y);
        }

        var decoded = new Vector3(x, y, z);
        return decoded.LengthSquared() > 1e-12f ? Vector3.Normalize(decoded) : Vector3.UnitZ;
    }

    private void PopulateEntries()
    {
        for (int triangle = 0; triangle < entries.Length; triangle++)
        {
            int index = triangle * 3;
            Vector3 a = PositionAt(VertexIndexAt(index));
            Vector3 b = PositionAt(VertexIndexAt(index + 1));
            Vector3 c = PositionAt(VertexIndexAt(index + 2));
            Vector3 minimum = Vector3.Min(a, Vector3.Min(b, c));
            Vector3 maximum = Vector3.Max(a, Vector3.Max(b, c));
            entries[triangle] = new TriangleEntry(
                triangle,
                minimum,
                maximum,
                (a + b + c) / 3f);
        }
    }

    internal IReadOnlyList<int> Query(
        Vector3 point,
        float maximumDistanceSquared,
        out HybridTerrainSurfaceQueryDiagnostics diagnostics)
    {
        var result = new List<int>();
        int nodeTests = 0;
        int triangleTests = 0;
        Collect(root, point, maximumDistanceSquared, result, ref nodeTests, ref triangleTests);
        result.Sort();
        diagnostics = new HybridTerrainSurfaceQueryDiagnostics(nodeTests, triangleTests);
        return result;
    }

    private SpatialNode BuildNode(int start, int count)
    {
        Vector3 minimum = new(float.PositiveInfinity);
        Vector3 maximum = new(float.NegativeInfinity);
        Vector3 centroidMinimum = new(float.PositiveInfinity);
        Vector3 centroidMaximum = new(float.NegativeInfinity);
        for (int index = start; index < start + count; index++)
        {
            TriangleEntry entry = entries[index];
            minimum = Vector3.Min(minimum, entry.Minimum);
            maximum = Vector3.Max(maximum, entry.Maximum);
            centroidMinimum = Vector3.Min(centroidMinimum, entry.Centroid);
            centroidMaximum = Vector3.Max(centroidMaximum, entry.Centroid);
        }

        if (count <= LeafCapacity)
        {
            return new SpatialNode(minimum, maximum, start, count, null, null);
        }

        Vector3 centroidExtent = centroidMaximum - centroidMinimum;
        int axis = centroidExtent.X >= centroidExtent.Y && centroidExtent.X >= centroidExtent.Z
            ? 0
            : centroidExtent.Y >= centroidExtent.Z
                ? 1
                : 2;
        Array.Sort(
            entries,
            start,
            count,
            Comparer<TriangleEntry>.Create(
                (left, right) => Component(left.Centroid, axis).CompareTo(Component(right.Centroid, axis))));
        int leftCount = count / 2;
        SpatialNode left = BuildNode(start, leftCount);
        SpatialNode right = BuildNode(start + leftCount, count - leftCount);
        return new SpatialNode(minimum, maximum, start, count, left, right);
    }

    private void Collect(
        SpatialNode node,
        Vector3 point,
        float maximumDistanceSquared,
        ICollection<int> result,
        ref int nodeTests,
        ref int triangleTests)
    {
        nodeTests++;
        if (DistanceSquaredToAabb(point, node.Minimum, node.Maximum) > maximumDistanceSquared)
        {
            return;
        }

        if (node.Left is not null && node.Right is not null)
        {
            Collect(node.Left, point, maximumDistanceSquared, result, ref nodeTests, ref triangleTests);
            Collect(node.Right, point, maximumDistanceSquared, result, ref nodeTests, ref triangleTests);
            return;
        }

        for (int index = node.Start; index < node.Start + node.Count; index++)
        {
            TriangleEntry entry = entries[index];
            if (DistanceSquaredToAabb(point, entry.Minimum, entry.Maximum) <= maximumDistanceSquared)
            {
                result.Add(entry.TriangleIndex);
                triangleTests++;
            }
        }
    }

    private static float DistanceSquaredToAabb(Vector3 point, Vector3 minimum, Vector3 maximum)
    {
        float dx = MathF.Max(MathF.Max(minimum.X - point.X, 0f), point.X - maximum.X);
        float dy = MathF.Max(MathF.Max(minimum.Y - point.Y, 0f), point.Y - maximum.Y);
        float dz = MathF.Max(MathF.Max(minimum.Z - point.Z, 0f), point.Z - maximum.Z);
        return (dx * dx) + (dy * dy) + (dz * dz);
    }

    private static float Component(Vector3 value, int axis) =>
        axis switch
        {
            0 => value.X,
            1 => value.Y,
            _ => value.Z,
        };

    private static float DecodeUnorm(ReadOnlySpan<byte> source, float minimum, float extent) =>
        minimum + ((BinaryPrimitives.ReadUInt16LittleEndian(source) / (float)ushort.MaxValue) * extent);

    private static float DecodeSnorm(short value) =>
        Math.Clamp(value / (float)short.MaxValue, -1f, 1f);

    private readonly record struct TriangleEntry(
        int TriangleIndex,
        Vector3 Minimum,
        Vector3 Maximum,
        Vector3 Centroid);

    private sealed record SpatialNode(
        Vector3 Minimum,
        Vector3 Maximum,
        int Start,
        int Count,
        SpatialNode? Left,
        SpatialNode? Right);
}
