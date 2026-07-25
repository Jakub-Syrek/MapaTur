using System.Numerics;

using MapaTur.Application.Terrain;
using MapaTur.Climbing;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// The rock skin is ONE continuous sculpted surface (crack systems + ledges + facets from a single
/// deterministic world-position field) draped slightly proud of the steep wall, with every hold blended
/// INTO it — the skin dips/rises so that at a hold's position its displacement equals the hold's exact
/// protrusion (the solver's ContactOffset), so hands land on rock that is part of the mountain, not on a
/// pile of separate blobs. Flat ground gets no skin at all (slope-gated), so meadows are never sculpted.
/// </summary>
public sealed class ClimbRockSkinMeshTests
{
    private static readonly Vector3 WallNormal = Vector3.Normalize(new Vector3(0f, -0.956f, 0.292f));

    // A steep planar wall rising 3 m per metre of +Y (~72°) — squarely climbing terrain.
    private static float? SteepGround(Vector2 xy) => 1500f + (3f * xy.Y);

    private static ClimbHold Hold(ClimbHoldType type, Vector3 position, string id = "h") =>
        new(id, position, WallNormal, 0.8f, type);

    // ── the continuous relief field ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Relief01_should_stay_inside_the_unit_interval_and_be_deterministic()
    {
        for (int i = 0; i < 500; i++)
        {
            var p = new Vector3(i * 0.37f, i * -0.21f, 1500f + (i * 0.11f));
            float value = ClimbRockReliefField.Relief01(p);

            Assert.InRange(value, 0f, 1f);
            Assert.Equal(value, ClimbRockReliefField.Relief01(p));
        }
    }

    [Fact]
    public void Relief01_should_carve_cracks_and_raise_ridges_over_a_wall_sized_region()
    {
        float min = float.MaxValue;
        float max = float.MinValue;
        for (int i = 0; i < 2000; i++)
        {
            var p = new Vector3((i % 45) * 0.43f, (i / 45) * 0.39f, 1500f + ((i % 71) * 0.27f));
            float value = ClimbRockReliefField.Relief01(p);
            min = MathF.Min(min, value);
            max = MathF.Max(max, value);
        }

        Assert.True(min < 0.25f, $"expected deep cracks, flattest sample {min:F2}");
        Assert.True(max > 0.75f, $"expected proud ridges, highest sample {max:F2}");
    }

    // ── the displacement law (single source of truth for the skin) ───────────────────────────────────────

    [Theory]
    [InlineData(ClimbHoldType.Jug)]
    [InlineData(ClimbHoldType.Crimp)]
    [InlineData(ClimbHoldType.FootEdge)]
    public void Displacement_at_a_hold_should_equal_its_exact_protrusion(ClimbHoldType type)
    {
        var position = new Vector3(12.3f, -4.5f, 1520f);
        ClimbHold hold = Hold(type, position);

        float displacement = ClimbRockSkinMesh.SurfaceDisplacementMeters(
            position, slopeGrade: 3f, [hold], ClimbWorld.Gravity);

        Assert.True(
            MathF.Abs(displacement - ClimbHoldImprintMesh.ProtrusionMeters(hold)) < 1e-3f,
            $"{type}: displacement {displacement:F4} m, expected {ClimbHoldImprintMesh.ProtrusionMeters(hold):F4} m");
    }

    [Fact]
    public void Displacement_far_from_holds_on_a_steep_wall_should_stay_inside_the_sculpt_band()
    {
        for (int i = 0; i < 300; i++)
        {
            var p = new Vector3(i * 0.61f, i * 0.17f, 1500f + (i * 0.23f));
            float displacement = ClimbRockSkinMesh.SurfaceDisplacementMeters(p, slopeGrade: 3f, [], ClimbWorld.Gravity);

            Assert.InRange(
                displacement,
                ClimbRockSkinMesh.BaseLiftMeters - 1e-4f,
                ClimbRockSkinMesh.BaseLiftMeters + ClimbRockSkinMesh.ReliefAmplitudeMeters + 1e-4f);
        }
    }

    [Fact]
    public void Displacement_on_flat_ground_without_holds_should_be_zero()
    {
        float displacement = ClimbRockSkinMesh.SurfaceDisplacementMeters(
            new Vector3(3f, 4f, 1200f), slopeGrade: 0.3f, [], ClimbWorld.Gravity);

        Assert.Equal(0f, displacement, 3);
    }

    // ── the built mesh ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Build_should_be_deterministic_for_the_same_window()
    {
        var holds = new[] { Hold(ClimbHoldType.Jug, new Vector3(1f, 2f, 1506f)) };

        ClimbRockSkin first = ClimbRockSkinMesh.Build(
            new Vector2(0f, 2f), halfSpanMeters: 6f, stepMeters: 0.25f, SteepGround, holds, ClimbWorld.Gravity);
        ClimbRockSkin second = ClimbRockSkinMesh.Build(
            new Vector2(0f, 2f), halfSpanMeters: 6f, stepMeters: 0.25f, SteepGround, holds, ClimbWorld.Gravity);

        Assert.True(first.Interleaved.SequenceEqual(second.Interleaved));
    }

    [Fact]
    public void Build_should_emit_no_triangles_over_flat_ground_without_holds()
    {
        ClimbRockSkin skin = ClimbRockSkinMesh.Build(
            new Vector2(0f, 0f), halfSpanMeters: 6f, stepMeters: 0.25f, _ => 900f, [], ClimbWorld.Gravity);

        Assert.Equal(0, skin.VertexCount);
    }

    [Fact]
    public void Build_should_cover_a_steep_wall_with_sculpted_triangles()
    {
        ClimbRockSkin skin = ClimbRockSkinMesh.Build(
            new Vector2(0f, 2f), halfSpanMeters: 6f, stepMeters: 0.25f, SteepGround, [], ClimbWorld.Gravity);

        Assert.True(skin.VertexCount > 3000, $"expected a dense skin, got {skin.VertexCount} vertices");
        Assert.Equal(skin.VertexCount * 9, skin.Interleaved.Length);
    }

    [Fact]
    public void Build_should_emit_unit_normals_on_the_outward_side_of_each_triangle()
    {
        ClimbRockSkin skin = ClimbRockSkinMesh.Build(
            new Vector2(0f, 2f), halfSpanMeters: 3f, stepMeters: 0.25f, SteepGround, [], ClimbWorld.Gravity);

        for (int t = 0; t + 27 <= skin.Interleaved.Length; t += 27)
        {
            Vector3 Read(int vertex, int offset) => new(
                skin.Interleaved[t + (vertex * 9) + offset],
                skin.Interleaved[t + (vertex * 9) + offset + 1],
                skin.Interleaved[t + (vertex * 9) + offset + 2]);

            Vector3 a = Read(0, 0), b = Read(1, 0), c = Read(2, 0);
            Vector3 stored = Read(0, 3);
            Vector3 face = Vector3.Cross(b - a, c - a);
            if (face.LengthSquared() < 1e-12f)
            {
                continue;
            }

            Assert.True(MathF.Abs(stored.Length() - 1f) < 1e-3f, "normal must be unit length");
            // Smooth (per-vertex) normals may deviate from the single face normal, but never flip side.
            Assert.True(Vector3.Dot(Vector3.Normalize(face), stored) > 0f, "normal must stay on the outward side");
        }
    }

    [Fact]
    public void Build_should_shade_smoothly_with_one_shared_normal_per_vertex_position()
    {
        // "Strasznie pixelowane" (user verdict on flat shading): every triangle carried its own facet
        // normal, so the whole wall glittered at the grid frequency. Smooth shading = every triangle
        // touching the same vertex position must store the SAME normal there.
        ClimbRockSkin skin = ClimbRockSkinMesh.Build(
            new Vector2(0f, 2f), halfSpanMeters: 3f, stepMeters: 0.25f, SteepGround, [], ClimbWorld.Gravity);

        Dictionary<(float, float, float), Vector3> seen = [];
        for (int v = 0; v + 9 <= skin.Interleaved.Length; v += 9)
        {
            (float, float, float) position = (skin.Interleaved[v], skin.Interleaved[v + 1], skin.Interleaved[v + 2]);
            var normal = new Vector3(skin.Interleaved[v + 3], skin.Interleaved[v + 4], skin.Interleaved[v + 5]);
            if (seen.TryGetValue(position, out Vector3 first))
            {
                Assert.True(
                    Vector3.Distance(first, normal) < 1e-4f,
                    $"vertex at {position} carries two different normals — flat shading leaked back in");
            }
            else
            {
                seen[position] = normal;
            }
        }
    }
}