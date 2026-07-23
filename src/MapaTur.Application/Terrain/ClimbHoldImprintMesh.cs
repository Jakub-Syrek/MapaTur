using System.Numerics;

using MapaTur.Climbing;

namespace MapaTur.Application.Terrain;

/// <summary>Flat-shaded triangle soup (3 floats per vertex, each vertex carrying its face normal) of one
/// hold's rock imprint, in climb space (real metres, absolute positions). The renderer applies vertical
/// exaggeration at draw time.</summary>
public sealed record ClimbHoldImprint(float[] Positions, float[] Normals);

/// <summary>
/// Materialises the geometry the climb physics already assumes: every <see cref="ClimbHold"/> carries a
/// <see cref="ClimbHold.ContactOffsetMeters"/> (how far the palm/toe sits off the wall), and until now that
/// distance was empty air over the smooth DEM. The imprint is a faceted granite feature whose apex sits
/// EXACTLY at that offset (so a gripping hand lands on rock), seated <see cref="SeatDepthMeters"/> behind
/// the wall (no floating gaps over the 0.5 m patch tessellation) and spanning the hold's usable width.
/// Fully deterministic per hold id + position — patch growth reproduces every imprint verbatim.
/// </summary>
public static class ClimbHoldImprintMesh
{
    /// <summary>How far the imprint reaches INTO the wall, so the coarse wall tessellation never shows a
    /// gap under the feature (the wall surface between patch vertices can sit up to ~6 cm off the plane).</summary>
    public const float SeatDepthMeters = 0.10f;

    /// <summary>
    /// Outward reach of the imprint along the hold normal. For grippable bulges this IS the hold's contact
    /// offset — the single source of truth the solver already uses — so hand and rock meet by definition.
    /// A pocket is a hole, not a bulge: until a later phase can carve the wall, it gets a shallow rim only.
    /// </summary>
    public static float ProtrusionMeters(ClimbHold hold) =>
        hold.Type == ClimbHoldType.Pocket ? 0.08f : hold.ContactOffsetMeters;

    /// <summary>Builds the imprint for one hold. Deterministic: same hold → identical arrays.</summary>
    public static ClimbHoldImprint Generate(ClimbHold hold, Vector3 gravity)
    {
        ArgumentNullException.ThrowIfNull(hold);

        ClimbSurfaceFrame frame = ClimbSurfaceFrame.Create(hold.Position, hold.Normal, gravity);
        (float halfWidth, float halfUp, float lobeAmplitude) = Proportions(hold);
        float protrusion = ProtrusionMeters(hold);

        (List<Vector3> verts, List<(int A, int B, int C)> faces) = ProceduralBoulderMesh.IcosphereGeometry(1);

        // Lobe phases + an in-plane spin from the hold id — neighbouring holds never share an orientation.
        int seed = StableSeed(hold.Id);
        float p1 = Unit(seed, 1) * MathF.Tau;
        float p2 = Unit(seed, 2) * MathF.Tau;
        float p3 = Unit(seed, 3) * MathF.Tau;
        float p4 = Unit(seed, 4) * MathF.Tau;
        float spin = Unit(seed, 5) * MathF.Tau;
        float spinCos = MathF.Cos(spin);
        float spinSin = MathF.Sin(spin);

        // Deep folding (user verdict on v1: "mocniejsze pofałdowanie"): roughly doubled lobe amplitudes plus
        // a fifth, higher-frequency term, so the blob reads as folded/broken granite instead of a soft potato.
        // The axis normalisation below still pins the exact extents, so stronger folds never move the apex.
        float p5 = Unit(seed, 6) * MathF.Tau;
        var local = new Vector3[verts.Count];
        for (int i = 0; i < verts.Count; i++)
        {
            Vector3 d = Vector3.Normalize(verts[i]);
            float r = 1f + (lobeAmplitude * (
                (0.38f * MathF.Sin((2.3f * d.X) + p1))
                + (0.30f * MathF.Sin((3.1f * d.Y) + p2))
                + (0.24f * MathF.Sin((2.7f * d.Z) + p3))
                + (0.19f * MathF.Sin((4.0f * (d.X + d.Y)) + p4))
                + (0.13f * MathF.Sin((6.3f * (d.Y + d.Z)) + p5))));
            r = MathF.Max(0.40f, r);
            Vector3 v = d * r;
            local[i] = new Vector3((v.X * spinCos) - (v.Y * spinSin), (v.X * spinSin) + (v.Y * spinCos), v.Z);
        }

        // Two-sided axis normalisation: pin the EXACT extents (apex = protrusion, base = -seat, tangential
        // span = usable width) while the lobes keep moving mass around inside them. This is what makes the
        // imprint testable against the solver's contact geometry instead of "roughly hold-sized".
        float maxPosX = 1e-4f, maxNegX = 1e-4f, maxPosY = 1e-4f, maxNegY = 1e-4f, maxPosZ = 1e-4f, maxNegZ = 1e-4f;
        foreach (Vector3 v in local)
        {
            if (v.X >= 0f) { maxPosX = MathF.Max(maxPosX, v.X); } else { maxNegX = MathF.Max(maxNegX, -v.X); }
            if (v.Y >= 0f) { maxPosY = MathF.Max(maxPosY, v.Y); } else { maxNegY = MathF.Max(maxNegY, -v.Y); }
            if (v.Z >= 0f) { maxPosZ = MathF.Max(maxPosZ, v.Z); } else { maxNegZ = MathF.Max(maxNegZ, -v.Z); }
        }

        var world = new Vector3[local.Length];
        for (int i = 0; i < local.Length; i++)
        {
            Vector3 v = local[i];
            float x = v.X >= 0f ? v.X / maxPosX * halfWidth : v.X / maxNegX * halfWidth;
            float y = v.Y >= 0f ? v.Y / maxPosY * halfUp : v.Y / maxNegY * halfUp;
            float z = v.Z >= 0f ? v.Z / maxPosZ * protrusion : v.Z / maxNegZ * SeatDepthMeters;
            world[i] = hold.Position
                + (frame.SideAlongSurface * x)
                + (frame.UpAlongSurface * y)
                + (frame.Normal * z);
        }

        var positions = new float[faces.Count * 9];
        var normals = new float[faces.Count * 9];
        int w = 0;
        foreach ((int a, int b, int c) in faces)
        {
            Vector3 va = world[a], vb = world[b], vc = world[c];
            Vector3 normal = Vector3.Cross(vb - va, vc - va);
            normal = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitZ;
            foreach (Vector3 v in stackalloc[] { va, vb, vc })
            {
                positions[w] = v.X; positions[w + 1] = v.Y; positions[w + 2] = v.Z;
                normals[w] = normal.X; normals[w + 1] = normal.Y; normals[w + 2] = normal.Z;
                w += 3;
            }
        }

        return new ClimbHoldImprint(positions, normals);
    }

    // Tangential half-extents + how strongly the lobes deform the blob. The tangential span always honours
    // the hold's UsableWidthMeters (two feet really fit on a 0.36 m edge); the up-extent and the lobe
    // amplitude give each type its silhouette: jug = fat knob, crimp/foot edge = squashed ledge lip,
    // sloper = smooth dome (half-amplitude lobes), pinch = tall narrow rib, pocket = small rim bump.
    private static (float HalfWidth, float HalfUp, float LobeAmplitude) Proportions(ClimbHold hold)
    {
        float halfWidth = hold.UsableWidthMeters * 0.5f;
        return hold.Type switch
        {
            ClimbHoldType.Crimp => (halfWidth, 0.05f, 1f),
            ClimbHoldType.Sloper => (halfWidth, 0.16f, 0.5f),
            ClimbHoldType.Pinch => (halfWidth, 0.20f, 1f),
            ClimbHoldType.Pocket => (0.12f, 0.10f, 1f),
            ClimbHoldType.FootEdge => (halfWidth, 0.045f, 0.8f),
            _ => (halfWidth, 0.14f, 1f)
        };
    }

    /// <summary>FNV-1a over the id — NEVER string.GetHashCode (randomised per process, would reshuffle
    /// every rock on restart).</summary>
    private static int StableSeed(string id)
    {
        unchecked
        {
            uint hash = 2166136261u;
            foreach (char c in id)
            {
                hash = (hash ^ c) * 16777619u;
            }

            return (int)hash;
        }
    }

    // Deterministic 0..1 from a seed + salt (same recipe as the boulder/terrain hashes).
    private static float Unit(int seed, int salt)
    {
        float v = MathF.Sin((seed * 12.9898f) + (salt * 78.233f)) * 43758.5453f;
        return v - MathF.Floor(v);
    }
}