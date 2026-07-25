using System.Numerics;

using MapaTur.Climbing;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Seeds a GUARANTEED ladder of holds along a catalogued climbing route: good-quality hand and foot
/// holds interleaved every ~0.38 m of 3D distance along the line (well inside limb reach, so the strict
/// planner can always continue), with a small deterministic side jitter so the climbing doesn't feel
/// like a literal rope of holds. Ids are derived from the route name + step index ONLY — stable across
/// patch rebuilds/growth, which mid-session surface replacement depends on.
/// </summary>
public static class ClimbingRouteHoldSeeder
{
    /// <summary>3D spacing between consecutive seeded holds (≤ 0.45 m keeps the strict planner fed).</summary>
    public const float HoldSpacingMeters = 0.38f;

    private const float WalkStepMeters = 0.05f;    // arc-length integration step along the route line
    private const float SideJitterMeters = 0.08f;  // deterministic lateral scatter off the exact line (small enough to keep the ≤0.45 m ladder spacing on steep ground)
    private const float NormalProbeMeters = 0.5f;  // central-difference step for the surface normal
    private const float HoldQuality = 0.9f;        // catalogued routes are climbed on GOOD holds

    // Hands + feet interleaved: every window of four consecutive holds contains both.
    private static readonly ClimbHoldType[] TypeCycle =
        [ClimbHoldType.Jug, ClimbHoldType.FootEdge, ClimbHoldType.Crimp, ClimbHoldType.FootEdge];

    /// <summary>
    /// Appends the route's holds that fall inside the current patch window to <paramref name="holds"/>.
    /// The step index — and therefore each hold's id and position — depends only on the route geometry,
    /// so a larger patch window reproduces the same holds verbatim plus new ones.
    /// </summary>
    public static void SeedHolds(
        WorldClimbingRoute route,
        Func<Vector2, float?> sampleGround,
        Func<Vector2, bool> insidePatch,
        List<ClimbHold> holds)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(sampleGround);
        ArgumentNullException.ThrowIfNull(insidePatch);
        ArgumentNullException.ThrowIfNull(holds);

        string slug = route.Name.ToLowerInvariant().Replace(' ', '-');
        int step = 0;
        float distanceSinceHold = 0f;
        float? previousElevation = null;
        Vector2? previousXY = null;

        for (int segment = 0; segment + 1 < route.PathXY.Count; segment++)
        {
            Vector2 a = route.PathXY[segment];
            Vector2 b = route.PathXY[segment + 1];
            float segmentLength = Vector2.Distance(a, b);
            if (segmentLength < 1e-4f)
            {
                continue;
            }

            Vector2 along = (b - a) / segmentLength;
            var side = new Vector2(along.Y, -along.X);
            int walkSteps = Math.Max(1, (int)MathF.Ceiling(segmentLength / WalkStepMeters));
            for (int i = segment == 0 ? 0 : 1; i <= walkSteps; i++)
            {
                Vector2 xy = a + (along * (segmentLength * (i / (float)walkSteps)));
                float? elevation = sampleGround(xy);
                if (elevation is null)
                {
                    previousXY = xy;
                    previousElevation = null;
                    continue;
                }

                if (previousXY is { } prevXY && previousElevation is { } prevZ)
                {
                    float dz = elevation.Value - prevZ;
                    distanceSinceHold += MathF.Sqrt(Vector2.DistanceSquared(prevXY, xy) + (dz * dz));
                }

                previousXY = xy;
                previousElevation = elevation;
                if (distanceSinceHold < HoldSpacingMeters && step > 0)
                {
                    continue;
                }

                distanceSinceHold = 0f;
                int index = step++;

                // Deterministic side jitter off the exact line (±SideJitterMeters), from the step index.
                float jitter = (Hash01(index * 0.7331f, index * 0.1913f) - 0.5f) * 2f * SideJitterMeters;
                Vector2 holdXY = xy + (side * jitter);
                float? holdElevation = sampleGround(holdXY);
                if (holdElevation is null)
                {
                    holdXY = xy;
                    holdElevation = elevation;
                }

                if (!insidePatch(holdXY))
                {
                    continue;
                }

                var position = new Vector3(holdXY.X, holdXY.Y, holdElevation.Value);
                ClimbHoldType type = TypeCycle[index % TypeCycle.Length];
                float? width = type == ClimbHoldType.FootEdge ? 0.36f : null;
                holds.Add(new ClimbHold(
                    $"route-{slug}-{index}", position, SurfaceNormal(sampleGround, holdXY, holdElevation.Value),
                    HoldQuality, type, width));
            }
        }
    }

    private static Vector3 SurfaceNormal(Func<Vector2, float?> sampleGround, Vector2 xy, float fallbackElevation)
    {
        float east = sampleGround(xy + new Vector2(NormalProbeMeters, 0f)) ?? fallbackElevation;
        float west = sampleGround(xy - new Vector2(NormalProbeMeters, 0f)) ?? fallbackElevation;
        float north = sampleGround(xy + new Vector2(0f, NormalProbeMeters)) ?? fallbackElevation;
        float south = sampleGround(xy - new Vector2(0f, NormalProbeMeters)) ?? fallbackElevation;
        var normal = new Vector3(west - east, south - north, 2f * NormalProbeMeters);
        return normal.LengthSquared() > 1e-8f ? Vector3.Normalize(normal) : Vector3.UnitZ;
    }

    private static float Hash01(float a, float b)
    {
        float value = MathF.Sin((a * 12.9898f) + (b * 78.233f)) * 43758.5453f;
        return value - MathF.Floor(value);
    }
}