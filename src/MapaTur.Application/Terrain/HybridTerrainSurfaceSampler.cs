using System.Numerics;

namespace MapaTur.Application.Terrain;

public readonly record struct HybridTerrainSurfaceSample(
    Vector3 Position,
    Vector3 Normal,
    int TriangleIndex,
    Vector3 Barycentric,
    float DistanceMeters);

/// <summary>
/// Shared surface query for objects previously seated on the DEM. The first implementation is deliberately
/// pure and exhaustive for offline/pilot verification; the runtime integration may put the same triangles
/// behind a page-local acceleration structure without changing this result contract.
/// </summary>
public static class HybridTerrainSurfaceSampler
{
    public static HybridTerrainSurfaceSample? SampleHybridSurface(
        HybridTerrainMesh mesh,
        Vector3 legacyPoint,
        float maxDistanceMeters)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ValidateQuery(legacyPoint, maxDistanceMeters);
        return SampleTriangles(
            mesh,
            Enumerable.Range(0, mesh.TriangleCount),
            legacyPoint,
            maxDistanceMeters);
    }

    public static HybridTerrainSurfaceSample? SampleHybridSurface(
        HybridTerrainSurfaceIndex index,
        Vector3 legacyPoint,
        float maxDistanceMeters,
        out HybridTerrainSurfaceQueryDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(index);
        ValidateQuery(legacyPoint, maxDistanceMeters);
        float maximumDistanceSquared = maxDistanceMeters * maxDistanceMeters;
        IReadOnlyList<int> candidates = index.Query(
            legacyPoint,
            maximumDistanceSquared,
            out diagnostics);
        return SampleIndexedTriangles(index, candidates, legacyPoint, maxDistanceMeters);
    }

    private static HybridTerrainSurfaceSample? SampleTriangles(
        HybridTerrainMesh mesh,
        IEnumerable<int> triangleIndices,
        Vector3 legacyPoint,
        float maxDistanceMeters)
    {
        float maximumDistanceSquared = maxDistanceMeters * maxDistanceMeters;
        float closestDistanceSquared = float.PositiveInfinity;
        HybridTerrainSurfaceSample? closest = null;
        foreach (int triangleIndex in triangleIndices)
        {
            int index = checked(triangleIndex * 3);
            int aIndex = checked((int)mesh.Indices[index]);
            int bIndex = checked((int)mesh.Indices[index + 1]);
            int cIndex = checked((int)mesh.Indices[index + 2]);
            Vector3 a = mesh.Positions[aIndex];
            Vector3 b = mesh.Positions[bIndex];
            Vector3 c = mesh.Positions[cIndex];
            if (!TryClosestPointOnTriangle(legacyPoint, a, b, c, out Vector3 point, out Vector3 barycentric))
            {
                continue;
            }

            float distanceSquared = Vector3.DistanceSquared(legacyPoint, point);
            if (distanceSquared > maximumDistanceSquared || distanceSquared >= closestDistanceSquared)
            {
                continue;
            }

            Vector3 normal =
                (mesh.Normals[aIndex] * barycentric.X)
                + (mesh.Normals[bIndex] * barycentric.Y)
                + (mesh.Normals[cIndex] * barycentric.Z);
            if (normal.LengthSquared() <= 1e-12f)
            {
                normal = Vector3.Cross(b - a, c - a);
            }

            normal = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitZ;
            closestDistanceSquared = distanceSquared;
            closest = new HybridTerrainSurfaceSample(
                point,
                normal,
                triangleIndex,
                barycentric,
                MathF.Sqrt(distanceSquared));
        }

        return closest;
    }

    private static HybridTerrainSurfaceSample? SampleIndexedTriangles(
        HybridTerrainSurfaceIndex index,
        IEnumerable<int> triangleIndices,
        Vector3 legacyPoint,
        float maxDistanceMeters)
    {
        float maximumDistanceSquared = maxDistanceMeters * maxDistanceMeters;
        float closestDistanceSquared = float.PositiveInfinity;
        HybridTerrainSurfaceSample? closest = null;
        foreach (int triangleIndex in triangleIndices)
        {
            int indexOffset = checked(triangleIndex * 3);
            int aIndex = index.VertexIndexAt(indexOffset);
            int bIndex = index.VertexIndexAt(indexOffset + 1);
            int cIndex = index.VertexIndexAt(indexOffset + 2);
            Vector3 a = index.PositionAt(aIndex);
            Vector3 b = index.PositionAt(bIndex);
            Vector3 c = index.PositionAt(cIndex);
            if (!TryClosestPointOnTriangle(legacyPoint, a, b, c, out Vector3 point, out Vector3 barycentric))
            {
                continue;
            }

            float distanceSquared = Vector3.DistanceSquared(legacyPoint, point);
            if (distanceSquared > maximumDistanceSquared || distanceSquared >= closestDistanceSquared)
            {
                continue;
            }

            Vector3 normal =
                (index.NormalAt(aIndex) * barycentric.X)
                + (index.NormalAt(bIndex) * barycentric.Y)
                + (index.NormalAt(cIndex) * barycentric.Z);
            if (normal.LengthSquared() <= 1e-12f)
            {
                normal = Vector3.Cross(b - a, c - a);
            }

            normal = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitZ;
            closestDistanceSquared = distanceSquared;
            closest = new HybridTerrainSurfaceSample(
                point,
                normal,
                triangleIndex,
                barycentric,
                MathF.Sqrt(distanceSquared));
        }

        return closest;
    }

    private static void ValidateQuery(Vector3 legacyPoint, float maxDistanceMeters)
    {
        if (!IsFinite(legacyPoint) || !float.IsFinite(maxDistanceMeters) || maxDistanceMeters < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDistanceMeters));
        }
    }

    private static bool TryClosestPointOnTriangle(
        Vector3 point,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        out Vector3 closest,
        out Vector3 barycentric)
    {
        Vector3 ab = b - a;
        Vector3 ac = c - a;
        if (Vector3.Cross(ab, ac).LengthSquared() <= 1e-12f)
        {
            closest = default;
            barycentric = default;
            return false;
        }

        Vector3 ap = point - a;
        float d1 = Vector3.Dot(ab, ap);
        float d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0f && d2 <= 0f)
        {
            closest = a;
            barycentric = Vector3.UnitX;
            return true;
        }

        Vector3 bp = point - b;
        float d3 = Vector3.Dot(ab, bp);
        float d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0f && d4 <= d3)
        {
            closest = b;
            barycentric = Vector3.UnitY;
            return true;
        }

        float vc = (d1 * d4) - (d3 * d2);
        if (vc <= 0f && d1 >= 0f && d3 <= 0f)
        {
            float v = d1 / (d1 - d3);
            closest = a + (v * ab);
            barycentric = new Vector3(1f - v, v, 0f);
            return true;
        }

        Vector3 cp = point - c;
        float d5 = Vector3.Dot(ab, cp);
        float d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0f && d5 <= d6)
        {
            closest = c;
            barycentric = Vector3.UnitZ;
            return true;
        }

        float vb = (d5 * d2) - (d1 * d6);
        if (vb <= 0f && d2 >= 0f && d6 <= 0f)
        {
            float w = d2 / (d2 - d6);
            closest = a + (w * ac);
            barycentric = new Vector3(1f - w, 0f, w);
            return true;
        }

        float va = (d3 * d6) - (d5 * d4);
        if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
        {
            float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
            closest = b + (w * (c - b));
            barycentric = new Vector3(0f, 1f - w, w);
            return true;
        }

        float denominator = 1f / (va + vb + vc);
        float insideV = vb * denominator;
        float insideW = vc * denominator;
        barycentric = new Vector3(1f - insideV - insideW, insideV, insideW);
        closest = a + (insideV * ab) + (insideW * ac);
        return true;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
