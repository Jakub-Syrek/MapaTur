using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>A faceted low-poly boulder: flat-shaded triangles (each vertex carries its face normal) so the
/// rock reads as angular granite/limestone blocks, not a smooth blob. Unit-ish size (~1 m across); the
/// scatter scales each instance.</summary>
public sealed class BoulderMesh
{
    public BoulderMesh(float[] positions, float[] normals, Vector3 halfExtents)
    {
        Positions = positions;
        Normals = normals;
        HalfExtents = halfExtents;
    }

    /// <summary>Flat-shaded triangle soup: 3 floats per vertex, 3 vertices per triangle, no index buffer
    /// (each triangle owns its vertices so the face normal is exact — the faceted granite look).</summary>
    public float[] Positions { get; }

    /// <summary>Per-vertex normal = the owning triangle's face normal.</summary>
    public float[] Normals { get; }

    public int VertexCount => Positions.Length / 3;

    /// <summary>Half-size of the axis-aligned bounds (for culling + seating on the ground).</summary>
    public Vector3 HalfExtents { get; }
}

/// <summary>
/// Procedurally generates a boulder from a seed — an icosphere pushed out by a few low-frequency lobes
/// (lumpy, not spiky), squashed vertically so it sits squat like a real block, then faceted. No external
/// asset: fully in-code and deterministic (same seed → same rock), so a scattered field never shimmers.
/// </summary>
public static class ProceduralBoulderMesh
{
    /// <summary>Builds a boulder. <paramref name="subdivisions"/> 1 ≈ 80 faces (low-poly, angular).</summary>
    public static BoulderMesh Generate(int seed, int subdivisions = 1)
    {
        (List<Vector3> verts, List<(int A, int B, int C)> faces) = Icosphere(Math.Clamp(subdivisions, 0, 3));

        // Four lobe phases + squat factor from the seed — deterministic, spatially coherent bumps.
        float p1 = Unit(seed, 1) * MathF.Tau;
        float p2 = Unit(seed, 2) * MathF.Tau;
        float p3 = Unit(seed, 3) * MathF.Tau;
        float p4 = Unit(seed, 4) * MathF.Tau;
        float squat = 0.62f + (0.18f * Unit(seed, 5)); // boulders are wider than tall

        var displaced = new Vector3[verts.Count];
        for (int i = 0; i < verts.Count; i++)
        {
            Vector3 d = Vector3.Normalize(verts[i]);
            float r = 1f
                + (0.20f * MathF.Sin((2.3f * d.X) + p1))
                + (0.16f * MathF.Sin((3.1f * d.Y) + p2))
                + (0.13f * MathF.Sin((2.7f * d.Z) + p3))
                + (0.10f * MathF.Sin((4.0f * (d.X + d.Y)) + p4));
            r = MathF.Max(0.45f, r);
            displaced[i] = new Vector3(d.X * r, d.Y * r, d.Z * r * squat);
        }

        var positions = new float[faces.Count * 9];
        var normals = new float[faces.Count * 9];
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        int w = 0;
        foreach ((int a, int b, int c) in faces)
        {
            Vector3 va = displaced[a], vb = displaced[b], vc = displaced[c];
            Vector3 normal = Vector3.Cross(vb - va, vc - va);
            normal = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitZ;
            foreach (Vector3 v in stackalloc[] { va, vb, vc })
            {
                positions[w] = v.X; positions[w + 1] = v.Y; positions[w + 2] = v.Z;
                normals[w] = normal.X; normals[w + 1] = normal.Y; normals[w + 2] = normal.Z;
                w += 3;
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }
        }

        Vector3 half = (max - min) * 0.5f;
        return new BoulderMesh(positions, normals, half);
    }

    // Unit icosahedron subdivided `subdivisions` times (midpoints re-projected to the sphere).
    // Internal: ClimbHoldImprintMesh deforms the same base sphere into per-hold rock features.
    internal static (List<Vector3> Verts, List<(int, int, int)> Faces) IcosphereGeometry(int subdivisions)
        => Icosphere(subdivisions);

    private static (List<Vector3> Verts, List<(int, int, int)> Faces) Icosphere(int subdivisions)
    {
        float t = (1f + MathF.Sqrt(5f)) * 0.5f;
        var verts = new List<Vector3>
        {
            Norm(-1, t, 0), Norm(1, t, 0), Norm(-1, -t, 0), Norm(1, -t, 0),
            Norm(0, -1, t), Norm(0, 1, t), Norm(0, -1, -t), Norm(0, 1, -t),
            Norm(t, 0, -1), Norm(t, 0, 1), Norm(-t, 0, -1), Norm(-t, 0, 1),
        };
        var faces = new List<(int, int, int)>
        {
            (0, 11, 5), (0, 5, 1), (0, 1, 7), (0, 7, 10), (0, 10, 11),
            (1, 5, 9), (5, 11, 4), (11, 10, 2), (10, 7, 6), (7, 1, 8),
            (3, 9, 4), (3, 4, 2), (3, 2, 6), (3, 6, 8), (3, 8, 9),
            (4, 9, 5), (2, 4, 11), (6, 2, 10), (8, 6, 7), (9, 8, 1),
        };

        for (int s = 0; s < subdivisions; s++)
        {
            var midpoint = new Dictionary<long, int>();
            var next = new List<(int, int, int)>(faces.Count * 4);
            foreach ((int a, int b, int c) in faces)
            {
                int ab = Midpoint(a, b, verts, midpoint);
                int bc = Midpoint(b, c, verts, midpoint);
                int ca = Midpoint(c, a, verts, midpoint);
                next.Add((a, ab, ca));
                next.Add((b, bc, ab));
                next.Add((c, ca, bc));
                next.Add((ab, bc, ca));
            }

            faces = next;
        }

        return (verts, faces);
    }

    private static int Midpoint(int a, int b, List<Vector3> verts, Dictionary<long, int> cache)
    {
        long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        if (cache.TryGetValue(key, out int existing))
        {
            return existing;
        }

        Vector3 m = Vector3.Normalize((verts[a] + verts[b]) * 0.5f);
        verts.Add(m);
        int index = verts.Count - 1;
        cache[key] = index;
        return index;
    }

    private static Vector3 Norm(float x, float y, float z) => Vector3.Normalize(new Vector3(x, y, z));

    // Deterministic 0..1 from a seed + salt (no RNG state → stable across runs).
    private static float Unit(int seed, int salt)
    {
        float v = MathF.Sin((seed * 12.9898f) + (salt * 78.233f)) * 43758.5453f;
        return v - MathF.Floor(v);
    }
}