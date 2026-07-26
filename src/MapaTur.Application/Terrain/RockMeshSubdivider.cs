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
/// Deterministic, seam-safe uniform triangle refinement for an offline rock page. Every source triangle in
/// the selected cliff patch receives the same subdivision level, so shared edges never acquire T-junctions.
/// </summary>
public static class RockMeshSubdivider
{
    private const int MaximumLevels = 12;

    public static IReadOnlyList<RockMeshTriangle> Subdivide(
        IReadOnlyList<RockMeshTriangle> source,
        float maximumEdgeMeters)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!float.IsFinite(maximumEdgeMeters) || maximumEdgeMeters <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEdgeMeters));
        }

        var current = source.ToList();
        for (int level = 0; level < MaximumLevels; level++)
        {
            float longest = current.Count == 0
                ? 0f
                : current.Max(triangle => triangle.EdgeLengths.Max());
            if (longest <= maximumEdgeMeters)
            {
                return current;
            }

            var midpoints = new Dictionary<EdgeKey, Vector3>();
            var next = new List<RockMeshTriangle>(checked(current.Count * 4));
            foreach (RockMeshTriangle triangle in current)
            {
                Vector3 ab = Midpoint(triangle.A, triangle.B, midpoints);
                Vector3 bc = Midpoint(triangle.B, triangle.C, midpoints);
                Vector3 ca = Midpoint(triangle.C, triangle.A, midpoints);
                next.Add(new RockMeshTriangle(triangle.A, ab, ca));
                next.Add(new RockMeshTriangle(ab, triangle.B, bc));
                next.Add(new RockMeshTriangle(ca, bc, triangle.C));
                next.Add(new RockMeshTriangle(ab, bc, ca));
            }

            current = next;
        }

        throw new InvalidOperationException(
            $"Rock page needs more than {MaximumLevels} subdivision levels for edge {maximumEdgeMeters} m.");
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
