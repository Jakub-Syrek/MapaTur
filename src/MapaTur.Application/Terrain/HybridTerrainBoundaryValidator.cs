using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Proves that the visible boundary of a replacement is the exact geometric boundary of the removed DEM hole.
/// Exact packed-source positions are intentional: a tolerance can hide T-junctions that later quantize to cracks.
/// </summary>
public static class HybridTerrainBoundaryValidator
{
    public static void EnsureReplacementWelded(
        HybridTerrainMesh terrain,
        HybridTerrainMesh replacement,
        IReadOnlySet<int> replacedTerrainTriangles)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentNullException.ThrowIfNull(replacement);
        ArgumentNullException.ThrowIfNull(replacedTerrainTriangles);

        var uses = new Dictionary<TopologicalEdge, EdgeUse>();
        for (int triangle = 0; triangle < terrain.TriangleCount; triangle++)
        {
            int source = triangle * 3;
            bool removed = replacedTerrainTriangles.Contains(triangle);
            AddUse(terrain.Indices[source], terrain.Indices[source + 1], removed);
            AddUse(terrain.Indices[source + 1], terrain.Indices[source + 2], removed);
            AddUse(terrain.Indices[source + 2], terrain.Indices[source], removed);
        }

        HashSet<GeometricEdge> terrainHoleBoundary = uses
            .Where(pair => pair.Value.Removed > 0 && (pair.Value.Kept > 0 || pair.Value.Removed == 1))
            .Select(pair => GeometricEdge.Create(
                terrain.Positions[checked((int)pair.Key.A)],
                terrain.Positions[checked((int)pair.Key.B)]))
            .ToHashSet();

        var replacementUses = new Dictionary<GeometricEdge, int>();
        for (int triangle = 0; triangle < replacement.TriangleCount; triangle++)
        {
            int source = triangle * 3;
            AddReplacementUse(replacement.Indices[source], replacement.Indices[source + 1]);
            AddReplacementUse(replacement.Indices[source + 1], replacement.Indices[source + 2]);
            AddReplacementUse(replacement.Indices[source + 2], replacement.Indices[source]);
        }

        if (replacementUses.Values.Any(count => count > 2))
        {
            throw new InvalidOperationException("RMP3 replacement contains a non-manifold boundary edge.");
        }

        HashSet<GeometricEdge> replacementBoundary = replacementUses
            .Where(pair => pair.Value == 1)
            .Select(pair => pair.Key)
            .ToHashSet();
        if (!terrainHoleBoundary.SetEquals(replacementBoundary))
        {
            throw new InvalidOperationException(
                "RMP3 replacement boundary does not match the removed DEM boundary bit-for-bit.");
        }

        void AddUse(uint first, uint second, bool removed)
        {
            TopologicalEdge edge = TopologicalEdge.Create(first, second);
            uses.TryGetValue(edge, out EdgeUse use);
            uses[edge] = removed
                ? use with { Removed = use.Removed + 1 }
                : use with { Kept = use.Kept + 1 };
        }

        void AddReplacementUse(uint first, uint second)
        {
            GeometricEdge edge = GeometricEdge.Create(
                replacement.Positions[checked((int)first)],
                replacement.Positions[checked((int)second)]);
            replacementUses.TryGetValue(edge, out int count);
            replacementUses[edge] = count + 1;
        }
    }

    private readonly record struct EdgeUse(int Removed, int Kept);

    private readonly record struct TopologicalEdge(uint A, uint B)
    {
        public static TopologicalEdge Create(uint first, uint second) =>
            first <= second ? new TopologicalEdge(first, second) : new TopologicalEdge(second, first);
    }

    private readonly record struct GeometricEdge(PositionKey A, PositionKey B)
    {
        public static GeometricEdge Create(Vector3 first, Vector3 second)
        {
            PositionKey a = PositionKey.Create(first);
            PositionKey b = PositionKey.Create(second);
            return a.CompareTo(b) <= 0 ? new GeometricEdge(a, b) : new GeometricEdge(b, a);
        }
    }

    private readonly record struct PositionKey(int X, int Y, int Z) : IComparable<PositionKey>
    {
        public static PositionKey Create(Vector3 value) => new(
            CanonicalBits(value.X),
            CanonicalBits(value.Y),
            CanonicalBits(value.Z));

        public int CompareTo(PositionKey other)
        {
            int x = X.CompareTo(other.X);
            if (x != 0)
            {
                return x;
            }

            int y = Y.CompareTo(other.Y);
            return y != 0 ? y : Z.CompareTo(other.Z);
        }

        private static int CanonicalBits(float value) =>
            value == 0f ? 0 : BitConverter.SingleToInt32Bits(value);
    }
}
