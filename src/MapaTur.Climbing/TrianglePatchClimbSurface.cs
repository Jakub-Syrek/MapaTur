using System.Numerics;

namespace MapaTur.Climbing;

/// <summary>Closest-point / ray hit on a climb surface patch, in climb space (real metres).</summary>
public readonly record struct SurfaceHit(Vector3 Position, Vector3 Normal, float Distance, int TriangleIndex);

/// <summary>
/// <see cref="IClimbSurface"/> over a <see cref="ClimbSurfacePatch"/>: BVH-accelerated closest point
/// and raycast with smooth interpolated normals, plus a uniform-grid radius query for holds.
/// Everything operates in climb space; callers convert render coordinates with <see cref="ClimbSpaceTransform"/>.
/// </summary>
public sealed class TrianglePatchClimbSurface : IClimbSurface
{
    private const float HoldGridCellMeters = 1.0f;

    private readonly BvhNode[] nodes;
    private readonly int[] orderedTriangles;
    private readonly Dictionary<(int X, int Y, int Z), List<int>> holdGrid;

    public TrianglePatchClimbSurface(ClimbSurfacePatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        Patch = patch;
        (nodes, orderedTriangles) = BuildBvh(patch);
        holdGrid = BuildHoldGrid(patch.Holds);
    }

    public ClimbSurfacePatch Patch { get; }

    public SurfaceHit ClosestPoint(Vector3 worldPosition)
    {
        float bestDistanceSquared = float.MaxValue;
        Vector3 bestPoint = default;
        int bestTriangle = -1;
        float bestV = 0f;
        float bestW = 0f;
        SearchClosest(0, worldPosition, ref bestDistanceSquared, ref bestPoint, ref bestTriangle, ref bestV, ref bestW);

        Vector3 normal = Patch.InterpolateNormal(bestTriangle, 1f - bestV - bestW, bestV, bestW);
        return new SurfaceHit(bestPoint, normal, MathF.Sqrt(bestDistanceSquared), bestTriangle);
    }

    public SurfaceHit? Raycast(Vector3 origin, Vector3 direction, float maxDistance = float.MaxValue)
    {
        if (direction.LengthSquared() < 1e-12f)
        {
            return null;
        }

        Vector3 unitDirection = Vector3.Normalize(direction);
        float bestDistance = maxDistance;
        int bestTriangle = -1;
        float bestV = 0f;
        float bestW = 0f;
        SearchRay(0, origin, unitDirection, ref bestDistance, ref bestTriangle, ref bestV, ref bestW);
        if (bestTriangle < 0)
        {
            return null;
        }

        Vector3 position = origin + (unitDirection * bestDistance);
        Vector3 normal = Patch.InterpolateNormal(bestTriangle, 1f - bestV - bestW, bestV, bestW);
        return new SurfaceHit(position, normal, bestDistance, bestTriangle);
    }

    public Vector3 ProjectToSurface(Vector3 worldPosition) => ClosestPoint(worldPosition).Position;

    public ClimbSurfaceFrame SampleSurface(Vector3 worldPosition, Vector3 gravity)
    {
        SurfaceHit hit = ClosestPoint(worldPosition);
        return ClimbSurfaceFrame.Create(hit.Position, hit.Normal, gravity);
    }

    public IReadOnlyList<ClimbHold> FindHolds(Vector3 worldPosition, float radius)
    {
        if (radius <= 0f || holdGrid.Count == 0)
        {
            return [];
        }

        List<ClimbHold> found = [];
        (int minX, int minY, int minZ) = CellOf(worldPosition - new Vector3(radius));
        (int maxX, int maxY, int maxZ) = CellOf(worldPosition + new Vector3(radius));
        float radiusSquared = radius * radius;
        for (int x = minX; x <= maxX; x++)
        {
            for (int y = minY; y <= maxY; y++)
            {
                for (int z = minZ; z <= maxZ; z++)
                {
                    if (!holdGrid.TryGetValue((x, y, z), out List<int>? bucket))
                    {
                        continue;
                    }

                    foreach (int holdIndex in bucket)
                    {
                        ClimbHold hold = Patch.Holds[holdIndex];
                        if (Vector3.DistanceSquared(hold.Position, worldPosition) <= radiusSquared)
                        {
                            found.Add(hold);
                        }
                    }
                }
            }
        }

        return found;
    }

    private void SearchClosest(
        int nodeIndex,
        Vector3 point,
        ref float bestDistanceSquared,
        ref Vector3 bestPoint,
        ref int bestTriangle,
        ref float bestV,
        ref float bestW)
    {
        BvhNode node = nodes[nodeIndex];
        if (DistanceSquaredToBox(point, node.Min, node.Max) >= bestDistanceSquared)
        {
            return;
        }

        if (node.Count > 0)
        {
            for (int i = 0; i < node.Count; i++)
            {
                int triangle = orderedTriangles[node.Start + i];
                Patch.GetTriangle(triangle, out Vector3 a, out Vector3 b, out Vector3 c);
                Vector3 candidate = ClosestPointOnTriangle(point, a, b, c, out float v, out float w);
                float distanceSquared = Vector3.DistanceSquared(point, candidate);
                if (distanceSquared < bestDistanceSquared)
                {
                    bestDistanceSquared = distanceSquared;
                    bestPoint = candidate;
                    bestTriangle = triangle;
                    bestV = v;
                    bestW = w;
                }
            }

            return;
        }

        // Visit the nearer child first so the far child can be pruned by the tightened bound.
        float leftDistance = DistanceSquaredToBox(point, nodes[node.Left].Min, nodes[node.Left].Max);
        float rightDistance = DistanceSquaredToBox(point, nodes[node.Right].Min, nodes[node.Right].Max);
        (int first, int second) = leftDistance <= rightDistance ? (node.Left, node.Right) : (node.Right, node.Left);
        SearchClosest(first, point, ref bestDistanceSquared, ref bestPoint, ref bestTriangle, ref bestV, ref bestW);
        SearchClosest(second, point, ref bestDistanceSquared, ref bestPoint, ref bestTriangle, ref bestV, ref bestW);
    }

    private void SearchRay(
        int nodeIndex,
        Vector3 origin,
        Vector3 direction,
        ref float bestDistance,
        ref int bestTriangle,
        ref float bestV,
        ref float bestW)
    {
        BvhNode node = nodes[nodeIndex];
        if (!RayIntersectsBox(origin, direction, node.Min, node.Max, bestDistance))
        {
            return;
        }

        if (node.Count > 0)
        {
            for (int i = 0; i < node.Count; i++)
            {
                int triangle = orderedTriangles[node.Start + i];
                Patch.GetTriangle(triangle, out Vector3 a, out Vector3 b, out Vector3 c);
                if (RayIntersectsTriangle(origin, direction, a, b, c, out float distance, out float v, out float w)
                    && distance < bestDistance)
                {
                    bestDistance = distance;
                    bestTriangle = triangle;
                    bestV = v;
                    bestW = w;
                }
            }

            return;
        }

        SearchRay(node.Left, origin, direction, ref bestDistance, ref bestTriangle, ref bestV, ref bestW);
        SearchRay(node.Right, origin, direction, ref bestDistance, ref bestTriangle, ref bestV, ref bestW);
    }

    /// <summary>Ericson, Real-Time Collision Detection 5.1.5; also returns barycentric v, w (u = 1 - v - w).</summary>
    private static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c, out float v, out float w)
    {
        Vector3 ab = b - a;
        Vector3 ac = c - a;
        Vector3 ap = p - a;
        float d1 = Vector3.Dot(ab, ap);
        float d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0f && d2 <= 0f)
        {
            v = 0f;
            w = 0f;
            return a;
        }

        Vector3 bp = p - b;
        float d3 = Vector3.Dot(ab, bp);
        float d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0f && d4 <= d3)
        {
            v = 1f;
            w = 0f;
            return b;
        }

        float vc = (d1 * d4) - (d3 * d2);
        if (vc <= 0f && d1 >= 0f && d3 <= 0f)
        {
            v = d1 / (d1 - d3);
            w = 0f;
            return a + (ab * v);
        }

        Vector3 cp = p - c;
        float d5 = Vector3.Dot(ab, cp);
        float d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0f && d5 <= d6)
        {
            v = 0f;
            w = 1f;
            return c;
        }

        float vb = (d5 * d2) - (d1 * d6);
        if (vb <= 0f && d2 >= 0f && d6 <= 0f)
        {
            v = 0f;
            w = d2 / (d2 - d6);
            return a + (ac * w);
        }

        float va = (d3 * d6) - (d5 * d4);
        if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
        {
            float t = (d4 - d3) / ((d4 - d3) + (d5 - d6));
            v = 1f - t;
            w = t;
            return b + ((c - b) * t);
        }

        float denominator = 1f / (va + vb + vc);
        v = vb * denominator;
        w = vc * denominator;
        return a + (ab * v) + (ac * w);
    }

    /// <summary>Moller-Trumbore, both faces (the patch decides orientation via its authored winding).</summary>
    private static bool RayIntersectsTriangle(
        Vector3 origin,
        Vector3 direction,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        out float distance,
        out float v,
        out float w)
    {
        distance = 0f;
        v = 0f;
        w = 0f;
        Vector3 ab = b - a;
        Vector3 ac = c - a;
        Vector3 pVec = Vector3.Cross(direction, ac);
        float determinant = Vector3.Dot(ab, pVec);
        if (MathF.Abs(determinant) < 1e-9f)
        {
            return false;
        }

        float inverseDeterminant = 1f / determinant;
        Vector3 tVec = origin - a;
        v = Vector3.Dot(tVec, pVec) * inverseDeterminant;
        if (v < 0f || v > 1f)
        {
            return false;
        }

        Vector3 qVec = Vector3.Cross(tVec, ab);
        w = Vector3.Dot(direction, qVec) * inverseDeterminant;
        if (w < 0f || v + w > 1f)
        {
            return false;
        }

        distance = Vector3.Dot(ac, qVec) * inverseDeterminant;
        return distance >= 0f;
    }

    private static float DistanceSquaredToBox(Vector3 point, Vector3 min, Vector3 max)
    {
        Vector3 clamped = Vector3.Clamp(point, min, max);
        return Vector3.DistanceSquared(point, clamped);
    }

    private static bool RayIntersectsBox(Vector3 origin, Vector3 direction, Vector3 min, Vector3 max, float maxDistance)
    {
        float tMin = 0f;
        float tMax = maxDistance;
        for (int axis = 0; axis < 3; axis++)
        {
            float o = Axis(origin, axis);
            float d = Axis(direction, axis);
            float lo = Axis(min, axis);
            float hi = Axis(max, axis);
            if (MathF.Abs(d) < 1e-9f)
            {
                if (o < lo || o > hi)
                {
                    return false;
                }

                continue;
            }

            float inverse = 1f / d;
            float t1 = (lo - o) * inverse;
            float t2 = (hi - o) * inverse;
            if (t1 > t2)
            {
                (t1, t2) = (t2, t1);
            }

            tMin = MathF.Max(tMin, t1);
            tMax = MathF.Min(tMax, t2);
            if (tMin > tMax)
            {
                return false;
            }
        }

        return true;
    }

    private static float Axis(Vector3 vector, int axis) => axis switch
    {
        0 => vector.X,
        1 => vector.Y,
        _ => vector.Z
    };

    private static (BvhNode[] Nodes, int[] Ordered) BuildBvh(ClimbSurfacePatch patch)
    {
        int triangleCount = patch.TriangleCount;
        var ordered = new int[triangleCount];
        var centroids = new Vector3[triangleCount];
        var mins = new Vector3[triangleCount];
        var maxs = new Vector3[triangleCount];
        for (int i = 0; i < triangleCount; i++)
        {
            ordered[i] = i;
            patch.GetTriangle(i, out Vector3 a, out Vector3 b, out Vector3 c);
            mins[i] = Vector3.Min(a, Vector3.Min(b, c));
            maxs[i] = Vector3.Max(a, Vector3.Max(b, c));
            centroids[i] = (a + b + c) / 3f;
        }

        List<BvhNode> nodes = [];
        BuildNode(nodes, ordered, centroids, mins, maxs, 0, triangleCount);
        return ([.. nodes], ordered);
    }

    private static int BuildNode(
        List<BvhNode> nodes,
        int[] ordered,
        Vector3[] centroids,
        Vector3[] mins,
        Vector3[] maxs,
        int start,
        int count)
    {
        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);
        for (int i = 0; i < count; i++)
        {
            int triangle = ordered[start + i];
            min = Vector3.Min(min, mins[triangle]);
            max = Vector3.Max(max, maxs[triangle]);
        }

        int nodeIndex = nodes.Count;
        nodes.Add(default);
        const int leafSize = 4;
        if (count <= leafSize)
        {
            nodes[nodeIndex] = new BvhNode(min, max, start, count, -1, -1);
            return nodeIndex;
        }

        Vector3 extent = max - min;
        int axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : (extent.Y >= extent.Z ? 1 : 2);
        Array.Sort(ordered, start, count, Comparer<int>.Create(
            (left, right) => Axis(centroids[left], axis).CompareTo(Axis(centroids[right], axis))));
        int half = count / 2;
        int left = BuildNode(nodes, ordered, centroids, mins, maxs, start, half);
        int right = BuildNode(nodes, ordered, centroids, mins, maxs, start + half, count - half);
        nodes[nodeIndex] = new BvhNode(min, max, 0, 0, left, right);
        return nodeIndex;
    }

    private static Dictionary<(int X, int Y, int Z), List<int>> BuildHoldGrid(IReadOnlyList<ClimbHold> holds)
    {
        Dictionary<(int X, int Y, int Z), List<int>> grid = [];
        for (int i = 0; i < holds.Count; i++)
        {
            (int X, int Y, int Z) cell = CellOf(holds[i].Position);
            if (!grid.TryGetValue(cell, out List<int>? bucket))
            {
                bucket = [];
                grid[cell] = bucket;
            }

            bucket.Add(i);
        }

        return grid;
    }

    private static (int X, int Y, int Z) CellOf(Vector3 position) => (
        (int)MathF.Floor(position.X / HoldGridCellMeters),
        (int)MathF.Floor(position.Y / HoldGridCellMeters),
        (int)MathF.Floor(position.Z / HoldGridCellMeters));

    private readonly record struct BvhNode(Vector3 Min, Vector3 Max, int Start, int Count, int Left, int Right);
}