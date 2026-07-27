using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Offline vertex clustering for scanned rock geometry. Spatial, normal and coarse UV keys retain overhang
/// layers, sharp creases and material islands while collapsing sub-pixel photogrammetry tessellation.
/// </summary>
public static class PhotogrammetryRockMeshClusterer
{
    private const float NormalBinsPerUnit = 2f;
    private const float UvBinsPerUnit = 2f;

    public static PhotogrammetryRockPrimitive Cluster(
        PhotogrammetryRockPrimitive source,
        float cellSizeMeters)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!float.IsFinite(cellSizeMeters) || cellSizeMeters <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(cellSizeMeters));
        }

        var spatialByKey = new Dictionary<SpatialKey, int>();
        var spatialAccumulators = new List<SpatialAccumulator>();
        var clusterByKey = new Dictionary<AttributeKey, int>();
        var accumulators = new List<Accumulator>();
        var clusterForSource = new int[source.Positions.Length];
        for (int vertex = 0; vertex < source.Positions.Length; vertex++)
        {
            Vector3 position = source.Positions[vertex];
            Vector3 normal = source.Normals[vertex];
            Vector2 uv = source.TexCoords[vertex];
            var spatialKey = new SpatialKey(
                Quantize(position.X, cellSizeMeters),
                Quantize(position.Y, cellSizeMeters),
                Quantize(position.Z, cellSizeMeters));
            if (!spatialByKey.TryGetValue(spatialKey, out int spatialCluster))
            {
                spatialCluster = spatialAccumulators.Count;
                spatialByKey.Add(spatialKey, spatialCluster);
                spatialAccumulators.Add(new SpatialAccumulator());
            }

            SpatialAccumulator spatial = spatialAccumulators[spatialCluster];
            spatial.Position += position;
            spatial.Count++;
            var key = new AttributeKey(
                spatialCluster,
                (int)MathF.Round(normal.X * NormalBinsPerUnit),
                (int)MathF.Round(normal.Y * NormalBinsPerUnit),
                (int)MathF.Round(normal.Z * NormalBinsPerUnit),
                (int)MathF.Floor(Math.Clamp(uv.X, 0f, 0.999999f) * UvBinsPerUnit),
                (int)MathF.Floor(Math.Clamp(uv.Y, 0f, 0.999999f) * UvBinsPerUnit));
            if (!clusterByKey.TryGetValue(key, out int cluster))
            {
                cluster = accumulators.Count;
                clusterByKey.Add(key, cluster);
                accumulators.Add(new Accumulator(spatialCluster));
            }

            Accumulator accumulator = accumulators[cluster];
            accumulator.Normal += normal;
            accumulator.Uv += uv;
            accumulator.SeamWeight = Math.Min(accumulator.SeamWeight, source.SeamWeights[vertex]);
            accumulator.Count++;
            clusterForSource[vertex] = cluster;
        }

        var triangles = new List<(int A, int B, int C)>(source.Indices.Length / 3);
        var uniqueTriangles = new HashSet<(int A, int B, int C)>();
        for (int index = 0; index < source.Indices.Length; index += 3)
        {
            int a = clusterForSource[checked((int)source.Indices[index])];
            int b = clusterForSource[checked((int)source.Indices[index + 1])];
            int c = clusterForSource[checked((int)source.Indices[index + 2])];
            if (a == b || b == c || a == c)
            {
                continue;
            }

            (int first, int second, int third) = Sort(a, b, c);
            if (uniqueTriangles.Add((first, second, third)))
            {
                triangles.Add((a, b, c));
            }
        }

        if (triangles.Count == 0)
        {
            throw new InvalidOperationException(
                "Clustering removed every triangle; use a smaller rock mesh cell.");
        }

        int[] usedClusters = triangles
            .SelectMany(triangle => new[] { triangle.A, triangle.B, triangle.C })
            .Distinct()
            .Order()
            .ToArray();
        var compactForCluster = new Dictionary<int, uint>(usedClusters.Length);
        var positions = new Vector3[usedClusters.Length];
        var normals = new Vector3[usedClusters.Length];
        var texCoords = new Vector2[usedClusters.Length];
        var seamWeights = new byte[usedClusters.Length];
        for (int compact = 0; compact < usedClusters.Length; compact++)
        {
            int cluster = usedClusters[compact];
            compactForCluster.Add(cluster, checked((uint)compact));
            Accumulator accumulator = accumulators[cluster];
            SpatialAccumulator spatial = spatialAccumulators[accumulator.SpatialCluster];
            positions[compact] = spatial.Position / spatial.Count;
            Vector3 normal = accumulator.Normal / accumulator.Count;
            normals[compact] = normal.LengthSquared() > 1e-10f
                ? Vector3.Normalize(normal)
                : Vector3.UnitZ;
            texCoords[compact] = accumulator.Uv / accumulator.Count;
            seamWeights[compact] = accumulator.SeamWeight;
        }

        var indices = new uint[triangles.Count * 3];
        for (int triangle = 0; triangle < triangles.Count; triangle++)
        {
            (int a, int b, int c) = triangles[triangle];
            indices[triangle * 3] = compactForCluster[a];
            indices[(triangle * 3) + 1] = compactForCluster[b];
            indices[(triangle * 3) + 2] = compactForCluster[c];
        }

        return new PhotogrammetryRockPrimitive(
            positions,
            normals,
            texCoords,
            indices,
            source.BaseColorImageBytes,
            seamWeights);
    }

    private static int Quantize(float value, float cellSizeMeters) =>
        (int)MathF.Floor(value / cellSizeMeters);

    private static (int First, int Second, int Third) Sort(int a, int b, int c)
    {
        if (a > b) { (a, b) = (b, a); }
        if (b > c) { (b, c) = (c, b); }
        if (a > b) { (a, b) = (b, a); }
        return (a, b, c);
    }

    private sealed class Accumulator
    {
        public Accumulator(int spatialCluster)
        {
            SpatialCluster = spatialCluster;
        }

        public int SpatialCluster { get; }
        public Vector3 Normal;
        public Vector2 Uv;
        public byte SeamWeight = byte.MaxValue;
        public int Count;
    }

    private sealed class SpatialAccumulator
    {
        public Vector3 Position;
        public int Count;
    }

    private readonly record struct SpatialKey(int X, int Y, int Z);

    private readonly record struct AttributeKey(
        int SpatialCluster,
        int NormalX,
        int NormalY,
        int NormalZ,
        int U,
        int V);
}
