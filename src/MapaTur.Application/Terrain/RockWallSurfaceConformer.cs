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
        Span<Candidate> nearest = stackalloc Candidate[8];
        int nearestCount = 0;
        for (int radius = 0; radius <= 3 && nearestCount < nearest.Length; radius++)
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
                        InsertNearest(
                            nearest,
                            ref nearestCount,
                            new Candidate(sample, (deltaU * deltaU) + (deltaV * deltaV)));
                    }
                }
            }
        }

        if (nearestCount == 0)
        {
            foreach (Sample sample in allSamples)
            {
                float deltaU = sample.U - u;
                float deltaV = sample.V - v;
                InsertNearest(
                    nearest,
                    ref nearestCount,
                    new Candidate(sample, (deltaU * deltaU) + (deltaV * deltaV)));
            }
        }

        float front = float.NegativeInfinity;
        for (int index = 0; index < nearestCount; index++)
        {
            front = MathF.Max(front, nearest[index].Sample.Depth);
        }

        float weighted = 0f;
        float totalWeight = 0f;
        int frontLayerCount = 0;
        for (int index = 0; index < nearestCount && frontLayerCount < 4; index++)
        {
            Candidate candidate = nearest[index];
            if (candidate.Sample.Depth < front - 2f)
            {
                continue;
            }

            float weight = 1f / MathF.Max(0.0001f, candidate.DistanceSquared);
            weighted += candidate.Sample.Depth * weight;
            totalWeight += weight;
            frontLayerCount++;
        }

        return weighted / totalWeight;
    }

    private static void InsertNearest(
        Span<Candidate> nearest,
        ref int count,
        Candidate candidate)
    {
        int insertionIndex;
        if (count < nearest.Length)
        {
            insertionIndex = count;
            count++;
        }
        else
        {
            if (candidate.DistanceSquared >= nearest[^1].DistanceSquared)
            {
                return;
            }

            insertionIndex = nearest.Length - 1;
        }

        while (insertionIndex > 0
            && candidate.DistanceSquared < nearest[insertionIndex - 1].DistanceSquared)
        {
            nearest[insertionIndex] = nearest[insertionIndex - 1];
            insertionIndex--;
        }

        nearest[insertionIndex] = candidate;
    }

    private (int U, int V) CellFor(float u, float v) =>
        ((int)MathF.Floor(u / cellSizeMeters), (int)MathF.Floor(v / cellSizeMeters));

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private readonly record struct Candidate(Sample Sample, float DistanceSquared);
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
        float edgeBlendFraction,
        float interiorClearanceMeters = 0f,
        IReadOnlyList<byte>? precomputedSeamWeights = null)
    {
        ArgumentNullException.ThrowIfNull(fitted);
        ArgumentNullException.ThrowIfNull(wall);
        if (!float.IsFinite(edgeBlendFraction) || edgeBlendFraction <= 0f || edgeBlendFraction > 0.5f)
        {
            throw new ArgumentOutOfRangeException(nameof(edgeBlendFraction));
        }

        if (!float.IsFinite(interiorClearanceMeters) || interiorClearanceMeters < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(interiorClearanceMeters));
        }

        if (precomputedSeamWeights is not null
            && precomputedSeamWeights.Count != fitted.Positions.Length)
        {
            throw new ArgumentException(
                "Precomputed seam weights must match the fitted vertex count.",
                nameof(precomputedSeamWeights));
        }

        Vector3 outward = Vector3.Normalize(placement.OutwardNormal);
        Vector3 up = Vector3.Normalize(Vector3.UnitZ - (outward * Vector3.Dot(Vector3.UnitZ, outward)));
        Vector3 tangent = Vector3.Normalize(Vector3.Cross(up, outward));
        byte[] seamWeights = precomputedSeamWeights?.ToArray()
            ?? CalculateWorldSeamWeights(fitted, tangent, up, edgeBlendFraction);
        float backingPlane = Vector3.Dot(placement.Center, outward);
        var positions = new Vector3[fitted.Positions.Length];
        for (int i = 0; i < positions.Length; i++)
        {
            Vector3 position = fitted.Positions[i];
            float measuredDepth = MathF.Max(0f, Vector3.Dot(position, outward) - backingPlane);
            float edgeMask = seamWeights[i] / (float)byte.MaxValue;
            float wallCoordinate = wall.SamplePlaneCoordinate(position);
            float desiredCoordinate =
                wallCoordinate + ((measuredDepth + interiorClearanceMeters) * edgeMask);
            positions[i] = position + (outward * (desiredCoordinate - Vector3.Dot(position, outward)));
        }

        Vector3[] normals = RecalculateNormals(positions, fitted.Indices, outward);
        return new PhotogrammetryRockPrimitive(
            positions,
            normals,
            fitted.TexCoords.ToArray(),
            fitted.Indices.ToArray(),
            fitted.BaseColorImageBytes,
            seamWeights);
    }

    /// <summary>
    /// Calculates the topology-aware outer-outline blend once in the scan's local XY frame. The result can be
    /// reused by every fitted, warped instance because those operations preserve vertex and triangle ordering.
    /// </summary>
    public static byte[] CalculateSourceSeamWeights(
        PhotogrammetryRockPrimitive source,
        float edgeBlendFraction)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateEdgeBlend(edgeBlendFraction);
        float minX = source.Positions.Min(position => position.X);
        float maxX = source.Positions.Max(position => position.X);
        float minY = source.Positions.Min(position => position.Y);
        float maxY = source.Positions.Max(position => position.Y);
        float blendDistance = MathF.Max(
            0.001f,
            MathF.Min(maxX - minX, maxY - minY) * edgeBlendFraction);
        return ToSeamWeights(
            CalculateBoundaryDistances(source.Positions, source.Indices),
            blendDistance);
    }

    private static byte[] CalculateWorldSeamWeights(
        PhotogrammetryRockPrimitive fitted,
        Vector3 tangent,
        Vector3 up,
        float edgeBlendFraction)
    {
        float[] tangentCoordinates = fitted.Positions
            .Select(position => Vector3.Dot(position, tangent))
            .ToArray();
        float[] upCoordinates = fitted.Positions
            .Select(position => Vector3.Dot(position, up))
            .ToArray();
        float blendDistance = MathF.Max(
            0.001f,
            MathF.Min(
                tangentCoordinates.Max() - tangentCoordinates.Min(),
                upCoordinates.Max() - upCoordinates.Min()) * edgeBlendFraction);
        return ToSeamWeights(
            CalculateBoundaryDistances(fitted.Positions, fitted.Indices),
            blendDistance);
    }

    private static byte[] ToSeamWeights(
        IReadOnlyList<float> distances,
        float blendDistance) =>
        distances
            .Select(distance => (byte)MathF.Round(
                SmoothStep(Math.Clamp(distance / blendDistance, 0f, 1f)) * byte.MaxValue))
            .ToArray();

    private static void ValidateEdgeBlend(float edgeBlendFraction)
    {
        if (!float.IsFinite(edgeBlendFraction) || edgeBlendFraction <= 0f || edgeBlendFraction > 0.5f)
        {
            throw new ArgumentOutOfRangeException(nameof(edgeBlendFraction));
        }
    }

    private static Vector3[] RecalculateNormals(
        IReadOnlyList<Vector3> positions,
        IReadOnlyList<uint> indices,
        Vector3 outward)
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
                : outward;
            if (Vector3.Dot(normals[i], outward) < 0f)
            {
                normals[i] = -normals[i];
            }
        }

        return normals;
    }

    private static float[] CalculateBoundaryDistances(
        IReadOnlyList<Vector3> positions,
        IReadOnlyList<uint> indices)
    {
        const float weldToleranceMeters = 0.001f;
        var weldedByPosition = new Dictionary<(int X, int Y, int Z), int>();
        var weldedPositions = new List<Vector3>();
        var weldedForVertex = new int[positions.Count];
        for (int vertex = 0; vertex < positions.Count; vertex++)
        {
            Vector3 position = positions[vertex];
            var key = (
                (int)MathF.Round(position.X / weldToleranceMeters),
                (int)MathF.Round(position.Y / weldToleranceMeters),
                (int)MathF.Round(position.Z / weldToleranceMeters));
            if (!weldedByPosition.TryGetValue(key, out int welded))
            {
                welded = weldedPositions.Count;
                weldedByPosition.Add(key, welded);
                weldedPositions.Add(position);
            }

            weldedForVertex[vertex] = welded;
        }

        var edgeCounts = new Dictionary<(int A, int B), int>();
        for (int i = 0; i < indices.Count; i += 3)
        {
            int a = weldedForVertex[checked((int)indices[i])];
            int b = weldedForVertex[checked((int)indices[i + 1])];
            int c = weldedForVertex[checked((int)indices[i + 2])];
            CountEdge(a, b, edgeCounts);
            CountEdge(b, c, edgeCounts);
            CountEdge(c, a, edgeCounts);
        }

        var adjacency = new List<(int Vertex, float Distance)>[weldedPositions.Count];
        for (int i = 0; i < adjacency.Length; i++)
        {
            adjacency[i] = [];
        }

        foreach ((int a, int b) in edgeCounts.Keys)
        {
            float distance = Vector3.Distance(weldedPositions[a], weldedPositions[b]);
            adjacency[a].Add((b, distance));
            adjacency[b].Add((a, distance));
        }

        var weldedDistances = Enumerable.Repeat(float.PositiveInfinity, weldedPositions.Count).ToArray();
        var queue = new PriorityQueue<int, float>();
        (int A, int B)[] boundaryEdges = edgeCounts
            .Where(edge => edge.Value == 1)
            .Select(edge => edge.Key)
            .ToArray();
        if (boundaryEdges.Length == 0)
        {
            return Enumerable.Repeat(float.PositiveInfinity, positions.Count).ToArray();
        }

        var boundaryAdjacency = new Dictionary<int, List<int>>();
        foreach ((int a, int b) in boundaryEdges)
        {
            AddBoundaryNeighbour(a, b);
            AddBoundaryNeighbour(b, a);
        }

        var visitedBoundary = new HashSet<int>();
        HashSet<int>? outerBoundary = null;
        float outerPerimeter = float.NegativeInfinity;
        foreach (int start in boundaryAdjacency.Keys)
        {
            if (!visitedBoundary.Add(start))
            {
                continue;
            }

            var component = new HashSet<int> { start };
            var pending = new Stack<int>();
            pending.Push(start);
            while (pending.TryPop(out int vertex))
            {
                foreach (int neighbour in boundaryAdjacency[vertex])
                {
                    if (visitedBoundary.Add(neighbour))
                    {
                        component.Add(neighbour);
                        pending.Push(neighbour);
                    }
                }
            }

            float perimeter = boundaryEdges
                .Where(edge => component.Contains(edge.A) && component.Contains(edge.B))
                .Sum(edge => Vector3.Distance(weldedPositions[edge.A], weldedPositions[edge.B]));
            if (perimeter > outerPerimeter)
            {
                outerPerimeter = perimeter;
                outerBoundary = component;
            }
        }

        foreach (int vertex in outerBoundary!)
        {
            Seed(vertex);
        }

        if (queue.Count == 0)
        {
            return Enumerable.Repeat(float.PositiveInfinity, positions.Count).ToArray();
        }

        while (queue.TryDequeue(out int vertex, out float queuedDistance))
        {
            if (queuedDistance > weldedDistances[vertex])
            {
                continue;
            }

            foreach ((int neighbour, float edgeDistance) in adjacency[vertex])
            {
                float candidate = queuedDistance + edgeDistance;
                if (candidate >= weldedDistances[neighbour])
                {
                    continue;
                }

                weldedDistances[neighbour] = candidate;
                queue.Enqueue(neighbour, candidate);
            }
        }

        return weldedForVertex.Select(welded => weldedDistances[welded]).ToArray();

        void Seed(int vertex)
        {
            if (weldedDistances[vertex] == 0f)
            {
                return;
            }

            weldedDistances[vertex] = 0f;
            queue.Enqueue(vertex, 0f);
        }

        void AddBoundaryNeighbour(int vertex, int neighbour)
        {
            if (!boundaryAdjacency.TryGetValue(vertex, out List<int>? neighbours))
            {
                neighbours = [];
                boundaryAdjacency.Add(vertex, neighbours);
            }

            neighbours.Add(neighbour);
        }
    }

    private static void CountEdge(
        int a,
        int b,
        IDictionary<(int A, int B), int> counts)
    {
        if (a == b)
        {
            return;
        }

        (int A, int B) edge = a < b ? (a, b) : (b, a);
        counts.TryGetValue(edge, out int count);
        counts[edge] = count + 1;
    }

    private static float SmoothStep(float value) => value * value * (3f - (2f * value));
}
