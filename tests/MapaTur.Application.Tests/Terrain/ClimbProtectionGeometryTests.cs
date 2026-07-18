using System.Numerics;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Visual auto-belay geometry: every planted piton carries a quickdraw hanging straight down
/// (sling + two carabiners), and the rope runs bolt → bottom carabiner of each quickdraw → harness
/// as a dense polyline with gravity sag between protection points and a nearly taut final segment.
/// Coordinates are render space (X-east/Y-north/Z-up); "down" is -Z.
/// </summary>
public sealed class ClimbProtectionGeometryTests
{
    private static readonly Vector3 Harness = new(0f, 0f, 10f);

    private static (List<ClimbProtectionGeometry.Quickdraw> Quickdraws, List<Vector3> Rope) Build(params Vector3[] anchors)
    {
        var quickdraws = new List<ClimbProtectionGeometry.Quickdraw>();
        var rope = new List<Vector3>();
        ClimbProtectionGeometry.Build(anchors, Harness, quickdraws, rope);
        return (quickdraws, rope);
    }

    private static float DistanceToPolyline(List<Vector3> points, Vector3 target)
    {
        float best = float.PositiveInfinity;
        for (int i = 0; i + 1 < points.Count; i++)
        {
            Vector3 a = points[i];
            Vector3 ab = points[i + 1] - a;
            float t = ab.LengthSquared() < 1e-12f ? 0f : Math.Clamp(Vector3.Dot(target - a, ab) / ab.LengthSquared(), 0f, 1f);
            best = MathF.Min(best, Vector3.Distance(target, a + (ab * t)));
        }

        return best;
    }

    [Fact]
    public void Build_should_produce_nothing_when_no_anchors()
    {
        (List<ClimbProtectionGeometry.Quickdraw> quickdraws, List<Vector3> rope) = Build();

        Assert.Empty(quickdraws);
        Assert.Empty(rope);
    }

    [Fact]
    public void Build_should_clear_previous_output_before_filling()
    {
        var quickdraws = new List<ClimbProtectionGeometry.Quickdraw> { default };
        var rope = new List<Vector3> { new(9f, 9f, 9f) };

        ClimbProtectionGeometry.Build([], Harness, quickdraws, rope);

        Assert.Empty(quickdraws);
        Assert.Empty(rope);
    }

    [Fact]
    public void Build_should_hang_bottom_carabiner_a_quickdraw_length_below_the_anchor()
    {
        var anchor = new Vector3(2f, 3f, 8f);

        (List<ClimbProtectionGeometry.Quickdraw> quickdraws, _) = Build(anchor);

        Vector3 expected = anchor with { Z = anchor.Z - ClimbProtectionGeometry.QuickdrawLengthMeters };
        Assert.Single(quickdraws);
        Assert.True(Vector3.Distance(quickdraws[0].BottomCarabiner, expected) < 1e-4f);
    }

    [Fact]
    public void Build_should_put_top_carabiner_between_anchor_and_bottom_carabiner()
    {
        var anchor = new Vector3(2f, 3f, 8f);

        (List<ClimbProtectionGeometry.Quickdraw> quickdraws, _) = Build(anchor);

        ClimbProtectionGeometry.Quickdraw quickdraw = quickdraws[0];
        Assert.True(quickdraw.TopCarabiner.Z < anchor.Z);
        Assert.True(quickdraw.TopCarabiner.Z > quickdraw.BottomCarabiner.Z);
        Assert.Equal(anchor.X, quickdraw.TopCarabiner.X, 4);
        Assert.Equal(anchor.Y, quickdraw.TopCarabiner.Y, 4);
    }

    [Fact]
    public void Build_should_start_rope_at_the_oldest_bottom_carabiner()
    {
        var first = new Vector3(0f, 0f, 4f);
        var second = new Vector3(1f, 0f, 7f);

        (List<ClimbProtectionGeometry.Quickdraw> quickdraws, List<Vector3> rope) = Build(first, second);

        Assert.True(Vector3.Distance(rope[0], quickdraws[0].BottomCarabiner) < 1e-4f);
    }

    [Fact]
    public void Build_should_end_rope_at_the_harness()
    {
        (_, List<Vector3> rope) = Build(new Vector3(0f, 0f, 4f));

        Assert.True(Vector3.Distance(rope[^1], Harness) < 1e-4f);
    }

    [Fact]
    public void Build_should_route_rope_through_every_bottom_carabiner()
    {
        (List<ClimbProtectionGeometry.Quickdraw> quickdraws, List<Vector3> rope) =
            Build(new Vector3(-2f, 0f, 3f), new Vector3(0f, 1f, 6f), new Vector3(2f, 0f, 9f));

        foreach (ClimbProtectionGeometry.Quickdraw quickdraw in quickdraws)
        {
            Assert.True(DistanceToPolyline(rope, quickdraw.BottomCarabiner) < 0.03f);
        }
    }

    [Fact]
    public void Build_should_sag_rope_below_the_chord_between_anchors()
    {
        var left = new Vector3(-2f, 0f, 8f);
        var right = new Vector3(2f, 0f, 8f);

        (List<ClimbProtectionGeometry.Quickdraw> quickdraws, List<Vector3> rope) = Build(left, right);

        // Midpoint of the chord between the two bottom carabiners (same height, 4 m apart).
        Vector3 chordMid = (quickdraws[0].BottomCarabiner + quickdraws[1].BottomCarabiner) * 0.5f;
        float lowestBetween = float.PositiveInfinity;
        foreach (Vector3 p in rope)
        {
            if (p.X > left.X + 0.5f && p.X < right.X - 0.5f)
            {
                lowestBetween = MathF.Min(lowestBetween, p.Z);
            }
        }

        float sag = chordMid.Z - lowestBetween;
        Assert.InRange(sag, 0.05f, 0.60f);
    }

    [Fact]
    public void Build_should_keep_the_final_segment_to_the_harness_nearly_taut()
    {
        // Off to the side so the chord to the harness is oblique — a vertical chord would hide sag entirely.
        var anchor = new Vector3(1.5f, 0f, 8.5f);

        (List<ClimbProtectionGeometry.Quickdraw> quickdraws, List<Vector3> rope) = Build(anchor);

        // Max vertical deviation of the rope below the straight chord bottom-carabiner → harness.
        Vector3 a = quickdraws[0].BottomCarabiner;
        Vector3 b = Harness;
        float worst = 0f;
        foreach (Vector3 p in rope)
        {
            float t = Math.Clamp(Vector3.Dot(p - a, b - a) / (b - a).LengthSquared(), 0f, 1f);
            Vector3 chord = Vector3.Lerp(a, b, t);
            worst = MathF.Max(worst, chord.Z - p.Z);
        }

        Assert.True(worst < 0.06f);
    }

    [Fact]
    public void Build_should_sample_the_rope_densely()
    {
        (_, List<Vector3> rope) = Build(new Vector3(-3f, 0f, 2f), new Vector3(3f, 0f, 9f));

        for (int i = 0; i + 1 < rope.Count; i++)
        {
            Assert.True(Vector3.Distance(rope[i], rope[i + 1]) <= 0.25f + 1e-3f);
        }
    }
}