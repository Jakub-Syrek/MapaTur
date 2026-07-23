using System.Numerics;

using MapaTur.Application.Terrain;
using MapaTur.Climbing;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// The hold imprint is the VISUAL materialisation of the geometry the climb physics already assumes:
/// a faceted rock feature whose apex sits EXACTLY at the hold's contact offset (so the gripping hand
/// lands on rock, not air), seated into the wall (no floating gaps over the 0.5 m tessellation), spanning
/// the hold's usable width, and fully deterministic per hold id (patch growth reproduces it verbatim).
/// </summary>
public sealed class ClimbHoldImprintMeshTests
{
    // A ~73° wall facing -Y (the Mnich east-face grade), so the imprint frame is exercised off-axis.
    private static readonly Vector3 WallNormal = Vector3.Normalize(new Vector3(0f, -0.956f, 0.292f));

    private static ClimbHold Hold(ClimbHoldType type, string id = "test-hold", float? width = null) =>
        new(id, new Vector3(120.5f, -44.25f, 1834.75f), WallNormal, 0.8f, type, width);

    private static ClimbHoldImprint Generate(ClimbHold hold) =>
        ClimbHoldImprintMesh.Generate(hold, ClimbWorld.Gravity);

    private static (float Min, float Max) ExtentAlong(ClimbHoldImprint imprint, ClimbHold hold, Vector3 axis)
    {
        float min = float.MaxValue;
        float max = float.MinValue;
        for (int i = 0; i < imprint.Positions.Length; i += 3)
        {
            var vertex = new Vector3(imprint.Positions[i], imprint.Positions[i + 1], imprint.Positions[i + 2]);
            float along = Vector3.Dot(vertex - hold.Position, axis);
            min = MathF.Min(min, along);
            max = MathF.Max(max, along);
        }

        return (min, max);
    }

    [Theory]
    [InlineData(ClimbHoldType.Jug)]
    [InlineData(ClimbHoldType.Crimp)]
    [InlineData(ClimbHoldType.Sloper)]
    [InlineData(ClimbHoldType.FootEdge)]
    [InlineData(ClimbHoldType.Pinch)]
    public void Generate_should_place_the_apex_exactly_at_the_protrusion_distance(ClimbHoldType type)
    {
        ClimbHold hold = Hold(type);

        (_, float apex) = ExtentAlong(Generate(hold), hold, hold.Normal);

        Assert.True(
            MathF.Abs(apex - ClimbHoldImprintMesh.ProtrusionMeters(hold)) < 1e-3f,
            $"{type}: apex {apex:F4} m, expected {ClimbHoldImprintMesh.ProtrusionMeters(hold):F4} m");
    }

    [Theory]
    [InlineData(ClimbHoldType.Jug)]
    [InlineData(ClimbHoldType.Crimp)]
    [InlineData(ClimbHoldType.Sloper)]
    [InlineData(ClimbHoldType.Pinch)]
    public void ProtrusionMeters_should_equal_the_holds_contact_offset_for_grippable_types(ClimbHoldType type)
    {
        ClimbHold hold = Hold(type);

        Assert.Equal(hold.ContactOffsetMeters, ClimbHoldImprintMesh.ProtrusionMeters(hold), 3);
    }

    [Fact]
    public void Generate_should_seat_the_base_exactly_seat_depth_behind_the_wall()
    {
        ClimbHold hold = Hold(ClimbHoldType.Jug);

        (float seat, _) = ExtentAlong(Generate(hold), hold, hold.Normal);

        Assert.True(
            MathF.Abs(seat + ClimbHoldImprintMesh.SeatDepthMeters) < 1e-3f,
            $"seat {seat:F4} m, expected {-ClimbHoldImprintMesh.SeatDepthMeters:F4} m");
    }

    [Fact]
    public void Generate_should_span_the_usable_width_for_a_wide_foot_edge()
    {
        ClimbHold hold = Hold(ClimbHoldType.FootEdge, width: 0.36f);
        ClimbSurfaceFrame frame = ClimbSurfaceFrame.Create(hold.Position, hold.Normal, ClimbWorld.Gravity);

        (float left, float right) = ExtentAlong(Generate(hold), hold, frame.SideAlongSurface);

        Assert.True(
            MathF.Abs((right - left) - hold.UsableWidthMeters) < 1e-3f,
            $"span {right - left:F4} m, expected {hold.UsableWidthMeters:F4} m");
    }

    [Fact]
    public void Generate_should_be_deterministic_for_the_same_hold()
    {
        ClimbHold hold = Hold(ClimbHoldType.Crimp, id: "route-mnich-7");

        ClimbHoldImprint first = Generate(hold);
        ClimbHoldImprint second = Generate(hold);

        Assert.True(first.Positions.SequenceEqual(second.Positions));
        Assert.True(first.Normals.SequenceEqual(second.Normals));
    }

    [Fact]
    public void Generate_should_vary_the_shape_between_different_hold_ids()
    {
        ClimbHold one = Hold(ClimbHoldType.Jug, id: "terrain-1.0-2.0-3.0");
        ClimbHold other = Hold(ClimbHoldType.Jug, id: "terrain-4.0-5.0-6.0");

        Assert.False(Generate(one).Positions.SequenceEqual(Generate(other).Positions));
    }

    [Fact]
    public void Generate_should_emit_flat_shaded_triangles_with_unit_normals_matching_the_winding()
    {
        ClimbHold hold = Hold(ClimbHoldType.Sloper);

        ClimbHoldImprint imprint = Generate(hold);

        Assert.True(imprint.Positions.Length > 0 && imprint.Positions.Length % 9 == 0);
        Assert.Equal(imprint.Positions.Length, imprint.Normals.Length);
        for (int t = 0; t < imprint.Positions.Length; t += 9)
        {
            var a = new Vector3(imprint.Positions[t], imprint.Positions[t + 1], imprint.Positions[t + 2]);
            var b = new Vector3(imprint.Positions[t + 3], imprint.Positions[t + 4], imprint.Positions[t + 5]);
            var c = new Vector3(imprint.Positions[t + 6], imprint.Positions[t + 7], imprint.Positions[t + 8]);
            var stored = new Vector3(imprint.Normals[t], imprint.Normals[t + 1], imprint.Normals[t + 2]);
            Vector3 face = Vector3.Cross(b - a, c - a);
            if (face.LengthSquared() < 1e-12f)
            {
                continue; // degenerate sliver: any unit normal is acceptable
            }

            Assert.True(MathF.Abs(stored.Length() - 1f) < 1e-3f, "normal must be unit length");
            Assert.True(
                Vector3.Dot(Vector3.Normalize(face), stored) > 0.999f,
                "stored normal must match the triangle winding");
        }
    }
}