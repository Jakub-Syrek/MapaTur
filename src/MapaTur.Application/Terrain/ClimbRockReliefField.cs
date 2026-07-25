using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// THE single deterministic rock-relief field (plan F2, docs/PLAN-climbing-rock-relief.md §2): one pure
/// function of world position that every consumer — the visual rock skin today, the physics patch
/// displacement in a later phase — must sample, so the sculpture can never diverge between them.
/// Models Tatra granite structure: two families of quasi-parallel joint planes (sub-vertical crack
/// systems), horizontal bedding breaks (ledges), and a faceted mezo field on a FIXED lattice with domain
/// warp (the granite v7 lesson: constant cell + jitter + warp, never a variable cell size).
/// </summary>
public static class ClimbRockReliefField
{
    /// <summary>Relief height in [0, 1]: 0 = crack bottom / notch, 1 = proud ridge face.</summary>
    public static float Relief01(Vector3 worldPos)
    {
        // Domain warp first — bends the joint planes and the facet lattice so nothing reads as a straight
        // synthetic line on the wall.
        float wx = 0.9f * MathF.Sin((0.31f * worldPos.Y) + (0.17f * worldPos.Z) + 1.3f);
        float wy = 0.9f * MathF.Sin((0.27f * worldPos.X) + (0.23f * worldPos.Z) + 4.1f);
        float wz = 0.7f * MathF.Sin((0.21f * worldPos.X) + (0.19f * worldPos.Y) + 2.6f);
        var q = new Vector3(worldPos.X + wx, worldPos.Y + wy, worldPos.Z + wz);

        // Joint families: distance to the nearest plane of each family carves a WIDE canyon ("za słaba
        // rzeźba" verdict: thin grooves read as shading lines, not geometry — the channels must be broad
        // enough to survive the 0.15 m lattice and the distance view). Spacings a few metres — climbable
        // crack/chimney rhythm.
        float crackA = CrackMask((q.X * 0.906f) + (q.Y * 0.423f) + (q.Z * 0.15f), spacing: 2.7f, core: 0.07f, edge: 0.40f);
        float crackB = CrackMask((q.X * -0.34f) + (q.Y * 0.94f) + (q.Z * 0.10f), spacing: 4.3f, core: 0.08f, edge: 0.45f);

        // Horizontal bedding breaks every ~3 m — the ledge/terrace rhythm of a big face.
        float bedding = CrackMask(q.Z, spacing: 3.1f, core: 0.10f, edge: 0.50f);

        // Blocky mezo field: large blocks (1.7 m lattice) + a second, finer octave (0.45 m) for rocky
        // roughness. Both trilinear on FIXED lattices; the smooth-shaded mesh turns them into rounded
        // boulder faces instead of grid-frequency glitter.
        float blocks = ValueNoise(q / 1.7f);
        float detail = ValueNoise(q / 0.45f);
        float facets = 0.28f + (0.50f * blocks) + (0.22f * detail);

        // Cracks multiply the blocky rock toward the canyon floor (never to absolute zero — a crack
        // bottom still has rock texture, just deep).
        return Math.Clamp(facets * (0.15f + (0.85f * (crackA * crackB * bedding))), 0f, 1f);
    }

    // 0 inside the crack core, rising smoothly to 1 at the crack edge. `along` is the coordinate across
    // the plane family in metres; planes sit at integer multiples of `spacing`.
    private static float CrackMask(float along, float spacing, float core, float edge)
    {
        float t = along / spacing;
        float distMeters = MathF.Abs(t - MathF.Round(t)) * spacing;
        return SmoothStep(core, edge, distMeters);
    }

    // Classic trilinear value noise over the unit lattice of `p` (deterministic corner hashes).
    private static float ValueNoise(Vector3 p)
    {
        int x0 = (int)MathF.Floor(p.X), y0 = (int)MathF.Floor(p.Y), z0 = (int)MathF.Floor(p.Z);
        float fx = Fade(p.X - x0), fy = Fade(p.Y - y0), fz = Fade(p.Z - z0);

        float c000 = Hash(x0, y0, z0), c100 = Hash(x0 + 1, y0, z0);
        float c010 = Hash(x0, y0 + 1, z0), c110 = Hash(x0 + 1, y0 + 1, z0);
        float c001 = Hash(x0, y0, z0 + 1), c101 = Hash(x0 + 1, y0, z0 + 1);
        float c011 = Hash(x0, y0 + 1, z0 + 1), c111 = Hash(x0 + 1, y0 + 1, z0 + 1);

        float x00 = c000 + ((c100 - c000) * fx);
        float x10 = c010 + ((c110 - c010) * fx);
        float x01 = c001 + ((c101 - c001) * fx);
        float x11 = c011 + ((c111 - c011) * fx);
        float y0v = x00 + ((x10 - x00) * fy);
        float y1v = x01 + ((x11 - x01) * fy);
        return y0v + ((y1v - y0v) * fz);
    }

    private static float Fade(float t) => t * t * (3f - (2f * t));

    private static float Hash(int x, int y, int z)
    {
        unchecked
        {
            uint h = (uint)x * 374761393u;
            h = (h + ((uint)y * 668265263u)) ^ (h >> 13);
            h = (h + ((uint)z * 2246822519u)) ^ (h >> 16);
            h *= 2654435761u;
            return (h & 0xFFFFFF) / (float)0x1000000;
        }
    }

    private static float SmoothStep(float lo, float hi, float v)
    {
        float t = Math.Clamp((v - lo) / (hi - lo), 0f, 1f);
        return t * t * (3f - (2f * t));
    }
}