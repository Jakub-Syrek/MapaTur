using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Builds the VISUAL geometry of the climb auto-belay from the planted piton anchors: a quickdraw
/// hanging straight down from every bolt (sling between two carabiners) and the rope as a dense
/// polyline threaded through the BOTTOM carabiners — not the bolts — sagging under gravity between
/// protection points and running nearly taut from the last quickdraw to the harness. Pure geometry
/// in render space (Z-up, elevations already exaggerated); physics owns the anchors, this only
/// dresses them for the renderer.
/// </summary>
public static class ClimbProtectionGeometry
{
    /// <summary>Bolt-to-bottom-carabiner length of a quickdraw (two carabiners + sling).</summary>
    public const float QuickdrawLengthMeters = 0.17f;

    /// <summary>Top carabiner centre hangs this far below the bolt (clipped into the hanger).</summary>
    public const float TopCarabinerDropMeters = 0.05f;

    /// <summary>Rope polyline sample step along each span.</summary>
    public const float RopeSampleStepMeters = 0.20f;

    // Sag depth as a fraction of the straight chord length: visible slack between protection points,
    // nearly taut on the live end to the harness (the auto-belay keeps that side snug).
    private const float SagBetweenAnchors = 0.035f;
    private const float SagToHarness = 0.012f;
    private const float MaxSagMeters = 0.60f;

    /// <summary>One quickdraw hanging from a piton bolt: the bolt itself, and the two carabiner centres.</summary>
    public readonly record struct Quickdraw(Vector3 Anchor, Vector3 TopCarabiner, Vector3 BottomCarabiner);

    /// <summary>Fills <paramref name="quickdraws"/> and <paramref name="ropePoints"/> (both cleared first)
    /// for the given bolts (oldest → newest) and the rope end at the climber's harness. No anchors →
    /// both lists stay empty (no rope is out).</summary>
    public static void Build(IReadOnlyList<Vector3> anchors, Vector3 harness, List<Quickdraw> quickdraws, List<Vector3> ropePoints)
    {
        ArgumentNullException.ThrowIfNull(anchors);
        ArgumentNullException.ThrowIfNull(quickdraws);
        ArgumentNullException.ThrowIfNull(ropePoints);

        quickdraws.Clear();
        ropePoints.Clear();
        if (anchors.Count == 0)
        {
            return;
        }

        foreach (Vector3 anchor in anchors)
        {
            quickdraws.Add(new Quickdraw(
                anchor,
                anchor with { Z = anchor.Z - TopCarabinerDropMeters },
                anchor with { Z = anchor.Z - QuickdrawLengthMeters }));
        }

        ropePoints.Add(quickdraws[0].BottomCarabiner);
        for (int i = 1; i < quickdraws.Count; i++)
        {
            AppendSaggingSpan(ropePoints, quickdraws[i - 1].BottomCarabiner, quickdraws[i].BottomCarabiner, SagBetweenAnchors);
        }

        AppendSaggingSpan(ropePoints, quickdraws[^1].BottomCarabiner, harness, SagToHarness);
    }

    // Appends one rope span a → b (excluding a, including b): points on the straight chord dropped by a
    // parabolic sag 4t(1−t) whose depth is sagFraction · |chord|, capped so long runs don't balloon.
    private static void AppendSaggingSpan(List<Vector3> ropePoints, Vector3 a, Vector3 b, float sagFraction)
    {
        float length = Vector3.Distance(a, b);
        float sag = MathF.Min(length * sagFraction, MaxSagMeters);
        int segments = Math.Max(1, (int)MathF.Ceiling(length / RopeSampleStepMeters));
        for (int s = 1; s <= segments; s++)
        {
            float t = s / (float)segments;
            Vector3 p = Vector3.Lerp(a, b, t);
            p.Z -= sag * 4f * t * (1f - t);
            ropePoints.Add(p);
        }
    }
}