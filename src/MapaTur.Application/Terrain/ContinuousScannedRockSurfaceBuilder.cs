using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Builds one shared, continuously displaced rock surface from steep DEM triangles.
/// The scan sampler supplies relief character, but never replaces or stamps over the DEM topology.
/// </summary>
public static class ContinuousScannedRockSurfaceBuilder
{
    private const float MinimumRockSlopeDegrees = 45f;
    private const float MaximumTangentJitterFraction = 0.16f;
    private const float BoundaryFadeMeters = 3f;

    public static PhotogrammetryRockPrimitive Build(
        IReadOnlyList<RockMeshTriangle> source,
        Func<Vector3, Vector3, RockSurfaceSample> sampleSurface,
        float sampleAmplitudeMeters,
        float maximumReliefMeters,
        float maximumEdgeMeters,
        int seed,
        byte[]? baseColorImageBytes)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sampleSurface);
        ValidatePositiveFinite(sampleAmplitudeMeters, nameof(sampleAmplitudeMeters));
        ValidatePositiveFinite(maximumReliefMeters, nameof(maximumReliefMeters));
        ValidatePositiveFinite(maximumEdgeMeters, nameof(maximumEdgeMeters));

        if (!source.Any(triangle => triangle.SlopeDegrees >= MinimumRockSlopeDegrees))
        {
            throw new InvalidOperationException("A continuous rock surface needs at least one steep DEM triangle.");
        }

        // Preserve every DEM triangle as one connected topological surface. Removing shallow triangles here
        // fragments a cliff into many alpha-faded islands; slope controls displacement/visibility below.
        IReadOnlyList<RockMeshTriangle> refined = RockMeshSubdivider.Subdivide(source, maximumEdgeMeters);
        BuildIndexedMesh(refined, out Vector3[] basePositions, out TriangleIndices[] triangles);
        Vector3[] baseNormals = CalculateNormals(basePositions, triangles);
        Vector3[] jittered = ApplyTangentJitter(basePositions, baseNormals, maximumEdgeMeters, seed);
        Vector3[] displaced = ApplyBoundedRelief(
            jittered,
            baseNormals,
            sampleSurface,
            sampleAmplitudeMeters,
            maximumReliefMeters);
        Vector3[] normals = CalculateNormals(displaced, triangles);
        Vector2[] texCoords = CalculateContinuousTexCoords(jittered, baseNormals);
        byte[] seamWeights = CalculateSeamWeights(
            basePositions,
            baseNormals,
            triangles,
            BoundaryFadeMeters);
        uint[] indices = triangles
            .SelectMany(triangle => new[] { (uint)triangle.A, (uint)triangle.B, (uint)triangle.C })
            .ToArray();

        return new PhotogrammetryRockPrimitive(
            displaced,
            normals,
            texCoords,
            indices,
            baseColorImageBytes,
            seamWeights);
    }

    private static void ValidatePositiveFinite(float value, string parameterName)
    {
        if (!float.IsFinite(value) || value <= 0f)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void BuildIndexedMesh(
        IReadOnlyList<RockMeshTriangle> source,
        out Vector3[] positions,
        out TriangleIndices[] triangles)
    {
        var positionList = new List<Vector3>();
        var triangleList = new List<TriangleIndices>(source.Count);
        var indexByPosition = new Dictionary<Vector3, int>();
        foreach (RockMeshTriangle triangle in source)
        {
            int a = GetOrAdd(triangle.A, positionList, indexByPosition);
            int b = GetOrAdd(triangle.B, positionList, indexByPosition);
            int c = GetOrAdd(triangle.C, positionList, indexByPosition);
            if (a != b && b != c && c != a)
            {
                triangleList.Add(new TriangleIndices(a, b, c));
            }
        }

        positions = positionList.ToArray();
        triangles = triangleList.ToArray();
    }

    private static int GetOrAdd(
        Vector3 position,
        ICollection<Vector3> positions,
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

    private static Vector3[] ApplyTangentJitter(
        IReadOnlyList<Vector3> positions,
        IReadOnlyList<Vector3> normals,
        float maximumEdgeMeters,
        int seed)
    {
        float maximumJitter = maximumEdgeMeters * MaximumTangentJitterFraction;
        var result = new Vector3[positions.Count];
        for (int i = 0; i < positions.Count; i++)
        {
            Vector3 normal = normals[i];
            Vector3 tangent = CreateTangent(normal);
            Vector3 bitangent = Vector3.Normalize(Vector3.Cross(normal, tangent));
            float alongTangent = SignedHash(positions[i], seed, 0x68bc21ebu) * maximumJitter;
            float alongBitangent = SignedHash(positions[i], seed, 0x02e5be93u) * maximumJitter;
            result[i] = positions[i] + (tangent * alongTangent) + (bitangent * alongBitangent);
        }

        return result;
    }

    private static Vector3 CreateTangent(Vector3 normal)
    {
        Vector3 reference = MathF.Abs(normal.Z) < 0.85f ? Vector3.UnitZ : Vector3.UnitY;
        Vector3 tangent = Vector3.Cross(reference, normal);
        return tangent.LengthSquared() > 1e-12f ? Vector3.Normalize(tangent) : Vector3.UnitX;
    }

    private static float SignedHash(Vector3 position, int seed, uint salt)
    {
        uint hash = unchecked((uint)seed) ^ salt;
        hash = Mix(hash ^ unchecked((uint)BitConverter.SingleToInt32Bits(position.X)));
        hash = Mix(hash ^ unchecked((uint)BitConverter.SingleToInt32Bits(position.Y)));
        hash = Mix(hash ^ unchecked((uint)BitConverter.SingleToInt32Bits(position.Z)));
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

    private static Vector3[] ApplyBoundedRelief(
        IReadOnlyList<Vector3> positions,
        IReadOnlyList<Vector3> baseNormals,
        Func<Vector3, Vector3, RockSurfaceSample> sampleSurface,
        float sampleAmplitudeMeters,
        float maximumReliefMeters)
    {
        var result = new Vector3[positions.Count];
        for (int i = 0; i < positions.Count; i++)
        {
            RockSurfaceSample sample = sampleSurface(positions[i], baseNormals[i]);
            if (!float.IsFinite(sample.DisplacementMeters))
            {
                throw new InvalidOperationException("Rock displacement sampler returned a non-finite value.");
            }

            float signedNormalized = sample.DisplacementMeters / sampleAmplitudeMeters;
            float outward = Math.Clamp((signedNormalized * 0.5f) + 0.5f, 0f, 1f);
            float relief = outward * outward * outward * maximumReliefMeters * CalculateSlopeWeight(baseNormals[i]);
            result[i] = positions[i] + (baseNormals[i] * relief);
        }

        return result;
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

    private static Vector2[] CalculateContinuousTexCoords(
        IReadOnlyList<Vector3> positions,
        IReadOnlyList<Vector3> normals)
    {
        Vector3 averageNormal = Vector3.Zero;
        foreach (Vector3 normal in normals)
        {
            averageNormal += normal;
        }

        averageNormal = averageNormal.LengthSquared() > 1e-12f
            ? Vector3.Normalize(averageNormal)
            : normals[0];
        Vector3 tangent = CreateTangent(averageNormal);
        Vector3 bitangent = Vector3.Normalize(Vector3.Cross(averageNormal, tangent));
        var projected = new Vector2[positions.Count];
        for (int i = 0; i < positions.Count; i++)
        {
            projected[i] = new Vector2(
                Vector3.Dot(positions[i], tangent),
                Vector3.Dot(positions[i], bitangent));
        }

        Vector2 minimum = projected.Aggregate(Vector2.Min);
        Vector2 maximum = projected.Aggregate(Vector2.Max);
        Vector2 extent = Vector2.Max(maximum - minimum, new Vector2(0.001f));
        for (int i = 0; i < projected.Length; i++)
        {
            projected[i] = (projected[i] - minimum) / extent;
        }

        return projected;
    }

    private static byte[] CalculateSeamWeights(
        IReadOnlyList<Vector3> positions,
        IReadOnlyList<Vector3> normals,
        IReadOnlyList<TriangleIndices> triangles,
        float boundaryFadeMeters)
    {
        var edgeCounts = new Dictionary<EdgeKey, byte>(triangles.Count * 2);
        var neighbours = new List<EdgeLink>?[positions.Count];
        foreach (TriangleIndices triangle in triangles)
        {
            AddEdge(triangle.A, triangle.B, positions, edgeCounts, neighbours);
            AddEdge(triangle.B, triangle.C, positions, edgeCounts, neighbours);
            AddEdge(triangle.C, triangle.A, positions, edgeCounts, neighbours);
        }

        var boundaryVertices = new HashSet<int>();
        foreach ((EdgeKey edge, byte count) in edgeCounts)
        {
            if (count == 1)
            {
                boundaryVertices.Add(edge.First);
                boundaryVertices.Add(edge.Second);
            }
        }

        float[] distanceToBoundary = CalculateDistanceToBoundary(
            positions.Count,
            neighbours,
            boundaryVertices);
        var result = new byte[normals.Count];
        for (int i = 0; i < normals.Count; i++)
        {
            float slope = MathF.Acos(Math.Clamp(MathF.Abs(normals[i].Z), 0f, 1f)) * (180f / MathF.PI);
            float slopeWeight = Math.Clamp((slope - MinimumRockSlopeDegrees) / 10f, 0f, 1f);
            float boundaryLinear = Math.Clamp(distanceToBoundary[i] / boundaryFadeMeters, 0f, 1f);
            float boundaryWeight = boundaryLinear * boundaryLinear * (3f - (2f * boundaryLinear));
            float weight = slopeWeight * boundaryWeight;
            result[i] = (byte)MathF.Round(weight * byte.MaxValue);
        }

        return result;
    }

    private static float CalculateSlopeWeight(Vector3 normal)
    {
        float slope = MathF.Acos(Math.Clamp(MathF.Abs(normal.Z), 0f, 1f)) * (180f / MathF.PI);
        return Math.Clamp((slope - MinimumRockSlopeDegrees) / 10f, 0f, 1f);
    }

    private static void AddEdge(
        int first,
        int second,
        IReadOnlyList<Vector3> positions,
        IDictionary<EdgeKey, byte> counts,
        IList<List<EdgeLink>?> neighbours)
    {
        EdgeKey key = EdgeKey.Create(first, second);
        counts[key] = counts.TryGetValue(key, out byte count)
            ? checked((byte)Math.Min(2, count + 1))
            : (byte)1;
        float length = Vector3.Distance(positions[first], positions[second]);
        (neighbours[first] ??= []).Add(new EdgeLink(second, length));
        (neighbours[second] ??= []).Add(new EdgeLink(first, length));
    }

    private static float[] CalculateDistanceToBoundary(
        int vertexCount,
        IReadOnlyList<List<EdgeLink>?> neighbours,
        IReadOnlySet<int> boundaryVertices)
    {
        var distances = Enumerable.Repeat(float.PositiveInfinity, vertexCount).ToArray();
        var pending = new PriorityQueue<int, float>();
        foreach (int boundary in boundaryVertices)
        {
            distances[boundary] = 0f;
            pending.Enqueue(boundary, 0f);
        }

        while (pending.TryDequeue(out int current, out float queuedDistance))
        {
            if (queuedDistance > distances[current])
            {
                continue;
            }

            foreach (EdgeLink edge in neighbours[current] ?? [])
            {
                float candidate = queuedDistance + edge.Length;
                if (candidate >= distances[edge.Neighbour])
                {
                    continue;
                }

                distances[edge.Neighbour] = candidate;
                pending.Enqueue(edge.Neighbour, candidate);
            }
        }

        return distances;
    }

    private readonly record struct TriangleIndices(int A, int B, int C);
    private readonly record struct EdgeLink(int Neighbour, float Length);

    private readonly record struct EdgeKey(int First, int Second)
    {
        public static EdgeKey Create(int first, int second) =>
            first <= second ? new EdgeKey(first, second) : new EdgeKey(second, first);
    }
}
