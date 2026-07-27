using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Sparse front-surface lookup in the local tangent/up frame of a steep DEM wall. Sampling returns the wall
/// coordinate along its outward normal, allowing a full 3D scan to follow a curved cliff without flattening.
/// </summary>
public sealed class RockWallSurfaceSampler
{
    private readonly Vector3 outward;
    private readonly Vector3 tangent;
    private readonly Vector3 up;
    private readonly float cellSizeMeters;
    private readonly Dictionary<(int U, int V), List<Sample>> cells = [];
    private readonly Sample[] allSamples;

    public RockWallSurfaceSampler(
        IEnumerable<Vector3> wallPoints,
        Vector3 outwardNormal,
        float cellSizeMeters)
    {
        ArgumentNullException.ThrowIfNull(wallPoints);
        if (!float.IsFinite(cellSizeMeters) || cellSizeMeters <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(cellSizeMeters));
        }

        if (!IsFinite(outwardNormal) || outwardNormal.LengthSquared() < 0.25f)
        {
            throw new ArgumentOutOfRangeException(nameof(outwardNormal));
        }

        outward = Vector3.Normalize(outwardNormal);
        up = Vector3.UnitZ - (outward * Vector3.Dot(Vector3.UnitZ, outward));
        if (up.LengthSquared() < 0.01f)
        {
            throw new ArgumentOutOfRangeException(nameof(outwardNormal), "Wall normal cannot be vertical.");
        }

        up = Vector3.Normalize(up);
        tangent = Vector3.Normalize(Vector3.Cross(up, outward));
        this.cellSizeMeters = cellSizeMeters;
        allSamples = wallPoints
            .Where(IsFinite)
            .Select(point => new Sample(
                Vector3.Dot(point, tangent),
                Vector3.Dot(point, up),
                Vector3.Dot(point, outward)))
            .ToArray();
        if (allSamples.Length == 0)
        {
            throw new ArgumentException("Wall sampler needs at least one finite point.", nameof(wallPoints));
        }

        foreach (Sample sample in allSamples)
        {
            (int U, int V) key = CellFor(sample.U, sample.V);
            if (!cells.TryGetValue(key, out List<Sample>? list))
            {
                list = [];
                cells.Add(key, list);
            }

            list.Add(sample);
        }
    }

    public float SamplePlaneCoordinate(Vector3 worldPosition)
    {
        float u = Vector3.Dot(worldPosition, tangent);
        float v = Vector3.Dot(worldPosition, up);
        (int centerU, int centerV) = CellFor(u, v);
        var candidates = new List<(Sample Sample, float DistanceSquared)>();
        for (int radius = 0; radius <= 3 && candidates.Count < 8; radius++)
        {
            for (int dv = -radius; dv <= radius; dv++)
            {
                for (int du = -radius; du <= radius; du++)
                {
                    if (radius > 0 && Math.Abs(du) != radius && Math.Abs(dv) != radius)
                    {
                        continue;
                    }

                    if (!cells.TryGetValue((centerU + du, centerV + dv), out List<Sample>? list))
                    {
                        continue;
                    }

                    foreach (Sample sample in list)
                    {
                        float deltaU = sample.U - u;
                        float deltaV = sample.V - v;
                        candidates.Add((sample, (deltaU * deltaU) + (deltaV * deltaV)));
                    }
                }
            }
        }

        if (candidates.Count == 0)
        {
            candidates.AddRange(allSamples.Select(sample =>
            {
                float deltaU = sample.U - u;
                float deltaV = sample.V - v;
                return (sample, (deltaU * deltaU) + (deltaV * deltaV));
            }));
        }

        (Sample Sample, float DistanceSquared)[] nearest = candidates
            .OrderBy(candidate => candidate.DistanceSquared)
            .Take(8)
            .ToArray();
        float front = nearest.Max(candidate => candidate.Sample.Depth);
        (Sample Sample, float DistanceSquared)[] frontLayer = nearest
            .Where(candidate => candidate.Sample.Depth >= front - 2f)
            .Take(4)
            .ToArray();
        float weighted = 0f;
        float totalWeight = 0f;
        foreach ((Sample sample, float distanceSquared) in frontLayer)
        {
            float weight = 1f / MathF.Max(0.0001f, distanceSquared);
            weighted += sample.Depth * weight;
            totalWeight += weight;
        }

        return weighted / totalWeight;
    }

    private (int U, int V) CellFor(float u, float v) =>
        ((int)MathF.Floor(u / cellSizeMeters), (int)MathF.Floor(v / cellSizeMeters));

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private readonly record struct Sample(float U, float V, float Depth);
}

/// <summary>
/// Drape-welds the backing plane of a fitted scan to the actual DEM wall while retaining its measured relief.
/// Relief fades only at the scan boundary, eliminating the floating rectangular plate seen in the first pilot.
/// </summary>
public static class RockWallSurfaceConformer
{
    public static PhotogrammetryRockPrimitive Conform(
        PhotogrammetryRockPrimitive fitted,
        RockScanPatchPlacement placement,
        RockWallSurfaceSampler wall,
        float edgeBlendFraction)
    {
        ArgumentNullException.ThrowIfNull(fitted);
        ArgumentNullException.ThrowIfNull(wall);
        if (!float.IsFinite(edgeBlendFraction) || edgeBlendFraction <= 0f || edgeBlendFraction > 0.5f)
        {
            throw new ArgumentOutOfRangeException(nameof(edgeBlendFraction));
        }

        Vector3 outward = Vector3.Normalize(placement.OutwardNormal);
        Vector3 up = Vector3.Normalize(Vector3.UnitZ - (outward * Vector3.Dot(Vector3.UnitZ, outward)));
        Vector3 tangent = Vector3.Normalize(Vector3.Cross(up, outward));
        float[] tangentCoordinates = fitted.Positions.Select(position => Vector3.Dot(position, tangent)).ToArray();
        float[] upCoordinates = fitted.Positions.Select(position => Vector3.Dot(position, up)).ToArray();
        float minTangent = tangentCoordinates.Min();
        float maxTangent = tangentCoordinates.Max();
        float minUp = upCoordinates.Min();
        float maxUp = upCoordinates.Max();
        float blendMeters = MathF.Max(
            0.001f,
            MathF.Min(maxTangent - minTangent, maxUp - minUp) * edgeBlendFraction);
        float[] distanceToBoundary = CalculateBoundaryDistances(fitted.Positions, fitted.Indices);
        float backingPlane = Vector3.Dot(placement.Center, outward);
        var positions = new Vector3[fitted.Positions.Length];
        for (int i = 0; i < positions.Length; i++)
        {
            Vector3 position = fitted.Positions[i];
            float measuredDepth = MathF.Max(0f, Vector3.Dot(position, outward) - backingPlane);
            float edgeMask = SmoothStep(Math.Clamp(distanceToBoundary[i] / blendMeters, 0f, 1f));
            float wallCoordinate = wall.SamplePlaneCoordinate(position);
            float desiredCoordinate = wallCoordinate + (measuredDepth * edgeMask);
            positions[i] = position + (outward * (desiredCoordinate - Vector3.Dot(position, outward)));
        }

        Vector3[] normals = RecalculateNormals(positions, fitted.Indices);
        return new PhotogrammetryRockPrimitive(
            positions,
            normals,
            fitted.TexCoords.ToArray(),
            fitted.Indices.ToArray(),
            fitted.BaseColorImageBytes);
    }

    private static Vector3[] RecalculateNormals(IReadOnlyList<Vector3> positions, IReadOnlyList<uint> indices)
    {
        var normals = new Vector3[positions.Count];
        for (int i = 0; i < indices.Count; i += 3)
        {
            int a = checked((int)indices[i]);
            int b = checked((int)indices[i + 1]);
            int c = checked((int)indices[i + 2]);
            Vector3 cross = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
            normals[a] += cross;
            normals[b] += cross;
            normals[c] += cross;
        }

        for (int i = 0; i < normals.Length; i++)
        {
            normals[i] = normals[i].LengthSquared() > 1e-10f
                ? Vector3.Normalize(normals[i])
                : Vector3.UnitZ;
        }

        return normals;
    }

    private static float[] CalculateBoundaryDistances(
        IReadOnlyList<Vector3> positions,
        IReadOnlyList<uint> indices)
    {
        var edgeCounts = new Dictionary<(int A, int B), int>();
        for (int i = 0; i < indices.Count; i += 3)
        {
            CountEdge(checked((int)indices[i]), checked((int)indices[i + 1]), edgeCounts);
            CountEdge(checked((int)indices[i + 1]), checked((int)indices[i + 2]), edgeCounts);
            CountEdge(checked((int)indices[i + 2]), checked((int)indices[i]), edgeCounts);
        }

        var adjacency = new List<(int Vertex, float Distance)>[positions.Count];
        for (int i = 0; i < adjacency.Length; i++)
        {
            adjacency[i] = [];
        }

        foreach ((int a, int b) in edgeCounts.Keys)
        {
            float distance = Vector3.Distance(positions[a], positions[b]);
            adjacency[a].Add((b, distance));
            adjacency[b].Add((a, distance));
        }

        var distances = Enumerable.Repeat(float.PositiveInfinity, positions.Count).ToArray();
        var queue = new PriorityQueue<int, float>();
        foreach (KeyValuePair<(int A, int B), int> edge in edgeCounts)
        {
            if (edge.Value != 1)
            {
                continue;
            }

            Seed(edge.Key.A);
            Seed(edge.Key.B);
        }

        if (queue.Count == 0)
        {
            return distances;
        }

        while (queue.TryDequeue(out int vertex, out float queuedDistance))
        {
            if (queuedDistance > distances[vertex])
            {
                continue;
            }

            foreach ((int neighbour, float edgeDistance) in adjacency[vertex])
            {
                float candidate = queuedDistance + edgeDistance;
                if (candidate >= distances[neighbour])
                {
                    continue;
                }

                distances[neighbour] = candidate;
                queue.Enqueue(neighbour, candidate);
            }
        }

        return distances;

        void Seed(int vertex)
        {
            if (distances[vertex] == 0f)
            {
                return;
            }

            distances[vertex] = 0f;
            queue.Enqueue(vertex, 0f);
        }
    }

    private static void CountEdge(
        int a,
        int b,
        IDictionary<(int A, int B), int> counts)
    {
        (int A, int B) edge = a < b ? (a, b) : (b, a);
        counts.TryGetValue(edge, out int count);
        counts[edge] = count + 1;
    }

    private static float SmoothStep(float value) => value * value * (3f - (2f * value));
}
