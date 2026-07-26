using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>One triangle in the offline rock-geometry pipeline.</summary>
public readonly record struct RockMeshTriangle(Vector3 A, Vector3 B, Vector3 C)
{
    public IReadOnlyList<Vector3> Vertices => new[] { A, B, C };

    public IReadOnlyList<float> EdgeLengths => new[]
    {
        Vector3.Distance(A, B),
        Vector3.Distance(B, C),
        Vector3.Distance(C, A),
    };

    public Vector3 Normal
    {
        get
        {
            Vector3 cross = Vector3.Cross(B - A, C - A);
            return cross.LengthSquared() > 1e-12f ? Vector3.Normalize(cross) : Vector3.UnitZ;
        }
    }

    public float SlopeDegrees =>
        MathF.Acos(Math.Clamp(MathF.Abs(Normal.Z), 0f, 1f)) * (180f / MathF.PI);
}

/// <summary>
/// Deterministic, seam-safe longest-edge refinement for an offline rock page. A midpoint is keyed by its exact
/// shared edge, so both triangles adjoining an overlong edge insert the same vertex while unrelated short edges
/// are not multiplied to match one tall DEM discontinuity.
/// </summary>
public static class RockMeshSubdivider
{
    private const int MaximumOutputTriangles = 10_000_000;

    public static IReadOnlyList<RockMeshTriangle> Subdivide(
        IReadOnlyList<RockMeshTriangle> source,
        float maximumEdgeMeters)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!float.IsFinite(maximumEdgeMeters) || maximumEdgeMeters <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEdgeMeters));
        }

        float maximumEdgeSquared = maximumEdgeMeters * maximumEdgeMeters;
        var midpoints = new Dictionary<EdgeKey, Vector3>();
        var pending = new Stack<RockMeshTriangle>(source.Reverse());
        var result = new List<RockMeshTriangle>(source.Count);
        while (pending.Count > 0)
        {
            RockMeshTriangle triangle = pending.Pop();
            float ab = Vector3.DistanceSquared(triangle.A, triangle.B);
            float bc = Vector3.DistanceSquared(triangle.B, triangle.C);
            float ca = Vector3.DistanceSquared(triangle.C, triangle.A);
            float longest = MathF.Max(ab, MathF.Max(bc, ca));
            if (longest <= maximumEdgeSquared * 1.000001f)
            {
                result.Add(triangle);
                continue;
            }

            RockMeshTriangle first;
            RockMeshTriangle second;
            if (ab >= bc && ab >= ca)
            {
                Vector3 midpointAb = Midpoint(triangle.A, triangle.B, midpoints);
                first = new RockMeshTriangle(triangle.A, midpointAb, triangle.C);
                second = new RockMeshTriangle(midpointAb, triangle.B, triangle.C);
            }
            else if (bc >= ca)
            {
                Vector3 midpointBc = Midpoint(triangle.B, triangle.C, midpoints);
                first = new RockMeshTriangle(triangle.A, triangle.B, midpointBc);
                second = new RockMeshTriangle(triangle.A, midpointBc, triangle.C);
            }
            else
            {
                Vector3 midpointCa = Midpoint(triangle.C, triangle.A, midpoints);
                first = new RockMeshTriangle(triangle.A, triangle.B, midpointCa);
                second = new RockMeshTriangle(midpointCa, triangle.B, triangle.C);
            }

            if (result.Count + pending.Count + 2 > MaximumOutputTriangles)
            {
                throw new InvalidOperationException(
                    $"Rock page refinement exceeded {MaximumOutputTriangles:N0} triangles.");
            }

            pending.Push(second);
            pending.Push(first);
        }

        return result;
    }

    private static Vector3 Midpoint(
        Vector3 a,
        Vector3 b,
        IDictionary<EdgeKey, Vector3> midpoints)
    {
        EdgeKey key = EdgeKey.Create(a, b);
        if (!midpoints.TryGetValue(key, out Vector3 midpoint))
        {
            midpoint = (a + b) * 0.5f;
            midpoints.Add(key, midpoint);
        }

        return midpoint;
    }

    private readonly record struct EdgeKey(Vector3 First, Vector3 Second)
    {
        public static EdgeKey Create(Vector3 a, Vector3 b) =>
            Compare(a, b) <= 0 ? new EdgeKey(a, b) : new EdgeKey(b, a);

        private static int Compare(Vector3 a, Vector3 b)
        {
            int x = a.X.CompareTo(b.X);
            if (x != 0)
            {
                return x;
            }

            int y = a.Y.CompareTo(b.Y);
            return y != 0 ? y : a.Z.CompareTo(b.Z);
        }
    }
}
