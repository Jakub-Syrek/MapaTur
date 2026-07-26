using System.Buffers.Binary;
using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>One deterministic sample used by the offline rock-geometry bake.</summary>
public readonly record struct RockSurfaceSample(
    float DisplacementMeters,
    byte AmbientOcclusion,
    ushort MaterialVariant)
{
    public static RockSurfaceSample Unchanged { get; } = new(0f, byte.MaxValue, 0);
}

/// <summary>
/// Converts steep source DEM triangles into a packed, GPU-ready RMP1 page. This code belongs to the
/// offline pipeline; the renderer only reads the resulting byte and index blocks.
/// </summary>
public static class RockMeshPageBaker
{
    public const float MinimumRockSlopeDegrees = 45f;
    private const float MinimumExtentMeters = 0.001f;
    private static readonly float[] TargetEdgeMeters = [0.25f, 0.5f, 1f];

    public static RockMeshPage Bake(
        byte lod,
        int pageX,
        int pageY,
        IReadOnlyList<RockMeshTriangle> source,
        Func<Vector3, Vector3, RockSurfaceSample> sampleSurface)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sampleSurface);
        if (lod >= TargetEdgeMeters.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(lod));
        }

        RockMeshTriangle[] steep = source
            .Where(triangle => triangle.SlopeDegrees >= MinimumRockSlopeDegrees)
            .ToArray();
        if (steep.Length == 0)
        {
            throw new InvalidOperationException("A rock mesh page needs at least one steep source triangle.");
        }

        float targetEdge = TargetEdgeMeters[lod];
        IReadOnlyList<RockMeshTriangle> refined = RockMeshSubdivider.Subdivide(steep, targetEdge);
        BuildIndexedMesh(refined, out List<Vector3> positions, out List<TriangleIndices> triangles);
        if (positions.Count > RockMeshPage.MaxVertices)
        {
            throw new InvalidOperationException(
                $"Refined page has {positions.Count} vertices; RMP1 permits {RockMeshPage.MaxVertices}.");
        }

        Vector3[] baseNormals = CalculateNormals(positions, triangles);
        var samples = new RockSurfaceSample[positions.Count];
        var displaced = new Vector3[positions.Count];
        float maximumDisplacement = 0f;
        for (int i = 0; i < positions.Count; i++)
        {
            RockSurfaceSample sample = sampleSurface(positions[i], baseNormals[i]);
            if (!float.IsFinite(sample.DisplacementMeters))
            {
                throw new InvalidOperationException("Rock displacement sampler returned a non-finite value.");
            }

            samples[i] = sample;
            displaced[i] = positions[i] + (baseNormals[i] * sample.DisplacementMeters);
            maximumDisplacement = MathF.Max(maximumDisplacement, MathF.Abs(sample.DisplacementMeters));
        }

        Vector3[] finalNormals = CalculateNormals(displaced, triangles);
        Vector3 minimum = displaced.Aggregate(Vector3.Min);
        Vector3 maximum = displaced.Aggregate(Vector3.Max);
        Vector3 extent = Vector3.Max(maximum - minimum, new Vector3(MinimumExtentMeters));
        byte[] vertices = PackVertices(displaced, finalNormals, baseNormals, samples, minimum, extent);
        ushort[] indices = PackIndices(triangles);

        return new RockMeshPage(
            lod,
            pageX,
            pageY,
            minimum,
            extent,
            geometricError: maximumDisplacement + (targetEdge * 0.5f),
            vertices,
            indices);
    }

    private static void BuildIndexedMesh(
        IReadOnlyList<RockMeshTriangle> triangles,
        out List<Vector3> positions,
        out List<TriangleIndices> indices)
    {
        positions = [];
        indices = new List<TriangleIndices>(triangles.Count);
        var indexByPosition = new Dictionary<Vector3, int>();
        foreach (RockMeshTriangle triangle in triangles)
        {
            int a = GetOrAdd(triangle.A, positions, indexByPosition);
            int b = GetOrAdd(triangle.B, positions, indexByPosition);
            int c = GetOrAdd(triangle.C, positions, indexByPosition);
            indices.Add(new TriangleIndices(a, b, c));
        }
    }

    private static int GetOrAdd(
        Vector3 position,
        List<Vector3> positions,
        IDictionary<Vector3, int> indexByPosition)
    {
        if (indexByPosition.TryGetValue(position, out int existing))
        {
            return existing;
        }

        int index = positions.Count;
        positions.Add(position);
        indexByPosition.Add(position, index);
        return index;
    }

    private static Vector3[] CalculateNormals(
        IReadOnlyList<Vector3> positions,
        IReadOnlyList<TriangleIndices> triangles)
    {
        var accumulated = new Vector3[positions.Count];
        foreach (TriangleIndices triangle in triangles)
        {
            Vector3 cross = Vector3.Cross(
                positions[triangle.B] - positions[triangle.A],
                positions[triangle.C] - positions[triangle.A]);
            accumulated[triangle.A] += cross;
            accumulated[triangle.B] += cross;
            accumulated[triangle.C] += cross;
        }

        for (int i = 0; i < accumulated.Length; i++)
        {
            accumulated[i] = accumulated[i].LengthSquared() > 1e-12f
                ? Vector3.Normalize(accumulated[i])
                : Vector3.UnitZ;
        }

        return accumulated;
    }

    private static byte[] PackVertices(
        IReadOnlyList<Vector3> positions,
        IReadOnlyList<Vector3> normals,
        IReadOnlyList<Vector3> baseNormals,
        IReadOnlyList<RockSurfaceSample> samples,
        Vector3 minimum,
        Vector3 extent)
    {
        var result = new byte[checked(positions.Count * RockMeshPage.VertexStrideBytes)];
        for (int i = 0; i < positions.Count; i++)
        {
            Span<byte> vertex = result.AsSpan(
                i * RockMeshPage.VertexStrideBytes,
                RockMeshPage.VertexStrideBytes);
            BinaryPrimitives.WriteUInt16LittleEndian(
                vertex,
                Quantize(positions[i].X, minimum.X, extent.X));
            BinaryPrimitives.WriteUInt16LittleEndian(
                vertex[2..],
                Quantize(positions[i].Y, minimum.Y, extent.Y));
            BinaryPrimitives.WriteUInt16LittleEndian(
                vertex[4..],
                Quantize(positions[i].Z, minimum.Z, extent.Z));

            Vector2 octahedral = EncodeOctahedral(normals[i]);
            BinaryPrimitives.WriteInt16LittleEndian(vertex[6..], PackSnorm(octahedral.X));
            BinaryPrimitives.WriteInt16LittleEndian(vertex[8..], PackSnorm(octahedral.Y));
            vertex[10] = samples[i].AmbientOcclusion;
            vertex[11] = CalculateTransition(baseNormals[i]);
            BinaryPrimitives.WriteUInt16LittleEndian(vertex[12..], samples[i].MaterialVariant);
        }

        return result;
    }

    private static ushort[] PackIndices(IReadOnlyList<TriangleIndices> triangles)
    {
        var result = new ushort[checked(triangles.Count * 3)];
        for (int i = 0; i < triangles.Count; i++)
        {
            result[(i * 3) + 0] = checked((ushort)triangles[i].A);
            result[(i * 3) + 1] = checked((ushort)triangles[i].B);
            result[(i * 3) + 2] = checked((ushort)triangles[i].C);
        }

        return result;
    }

    private static ushort Quantize(float value, float minimum, float extent)
    {
        float normalized = Math.Clamp((value - minimum) / extent, 0f, 1f);
        return (ushort)MathF.Round(normalized * ushort.MaxValue);
    }

    private static Vector2 EncodeOctahedral(Vector3 normal)
    {
        normal = Vector3.Normalize(normal);
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

    private static byte CalculateTransition(Vector3 normal)
    {
        float slope = MathF.Acos(Math.Clamp(MathF.Abs(normal.Z), 0f, 1f)) * (180f / MathF.PI);
        return (byte)MathF.Round(Math.Clamp((slope - MinimumRockSlopeDegrees) / 10f, 0f, 1f) * byte.MaxValue);
    }

    private readonly record struct TriangleIndices(int A, int B, int C);
}
