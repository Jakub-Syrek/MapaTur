using System.Collections.ObjectModel;
using System.Numerics;

namespace MapaTur.Climbing;

/// <summary>
/// Renderer-independent route-shape diagnostics. They distinguish a calibration ladder from a route that demands
/// traversing, direction changes and asymmetric body positions before an expensive rig/physics autorun is started.
/// </summary>
public sealed record ClimbRouteGeometrySummary(
    int StageCount,
    float VerticalGainMeters,
    float MeanHorizontalTravelPerLimbMeters,
    int HorizontalDirectionChanges,
    int AsymmetricStages,
    float LadderLikeStageFraction,
    float MaximumSingleLimbMoveMeters,
    IReadOnlyDictionary<ClimbLimb, float> HorizontalTravelByLimbMeters)
{
    public bool ExercisesNonLadderMovement => StageCount >= 3
        && MeanHorizontalTravelPerLimbMeters >= 0.75f
        && (HorizontalDirectionChanges > 0 || LadderLikeStageFraction <= 0.35f)
        && AsymmetricStages >= 2;
}

public static class ClimbRouteGeometry
{
    public static ClimbRouteGeometrySummary Analyze(
        IReadOnlyList<IReadOnlyDictionary<ClimbLimb, Vector3>> stages,
        Vector3 gravity,
        Vector3 representativeSurfaceNormal)
    {
        ArgumentNullException.ThrowIfNull(stages);
        if (stages.Count == 0)
        {
            throw new ArgumentException("A route must contain at least one contact stage.", nameof(stages));
        }

        ClimbLimb[] limbs = Enum.GetValues<ClimbLimb>();
        foreach (IReadOnlyDictionary<ClimbLimb, Vector3> stage in stages)
        {
            if (limbs.Any(limb => !stage.ContainsKey(limb)))
            {
                throw new ArgumentException("Every route stage must contain all four limbs.", nameof(stages));
            }
        }

        ClimbSurfaceFrame frame = ClimbSurfaceFrame.Create(
            Average(stages[0].Values),
            representativeSurfaceNormal,
            gravity);
        var horizontalTravel = limbs.ToDictionary(limb => limb, _ => 0f);
        int directionChanges = 0;
        int asymmetricStages = 0;
        int ladderLikeStages = 0;
        float maximumMove = 0f;
        float? previousHorizontalDirection = null;

        for (int stageIndex = 0; stageIndex < stages.Count; stageIndex++)
        {
            IReadOnlyDictionary<ClimbLimb, Vector3> stage = stages[stageIndex];
            float leftHandHeight = Vector3.Dot(stage[ClimbLimb.LeftHand], frame.UpAlongSurface);
            float rightHandHeight = Vector3.Dot(stage[ClimbLimb.RightHand], frame.UpAlongSurface);
            float leftFootHeight = Vector3.Dot(stage[ClimbLimb.LeftFoot], frame.UpAlongSurface);
            float rightFootHeight = Vector3.Dot(stage[ClimbLimb.RightFoot], frame.UpAlongSurface);
            if (MathF.Abs(leftHandHeight - rightHandHeight) >= 0.16f
                || MathF.Abs(leftFootHeight - rightFootHeight) >= 0.16f)
            {
                asymmetricStages++;
            }

            if (stageIndex == 0)
            {
                continue;
            }

            IReadOnlyDictionary<ClimbLimb, Vector3> previous = stages[stageIndex - 1];
            float[] horizontalDeltas = new float[limbs.Length];
            float[] verticalDeltas = new float[limbs.Length];
            for (int limbIndex = 0; limbIndex < limbs.Length; limbIndex++)
            {
                ClimbLimb limb = limbs[limbIndex];
                Vector3 delta = stage[limb] - previous[limb];
                horizontalDeltas[limbIndex] = Vector3.Dot(delta, frame.SideAlongSurface);
                verticalDeltas[limbIndex] = Vector3.Dot(delta, frame.UpAlongSurface);
                horizontalTravel[limb] += MathF.Abs(horizontalDeltas[limbIndex]);
                maximumMove = MathF.Max(maximumMove, delta.Length());
            }

            float meanHorizontalDelta = horizontalDeltas.Average();
            float horizontalDirection = MathF.Sign(meanHorizontalDelta);
            if (MathF.Abs(meanHorizontalDelta) >= 0.12f)
            {
                if (previousHorizontalDirection is { } previousDirection
                    && horizontalDirection != previousDirection)
                {
                    directionChanges++;
                }

                previousHorizontalDirection = horizontalDirection;
            }

            float horizontalSpread = horizontalDeltas.Max() - horizontalDeltas.Min();
            float verticalSpread = verticalDeltas.Max() - verticalDeltas.Min();
            bool mostlyUp = verticalDeltas.Average() >= 0.20f;
            bool littleTraverse = MathF.Abs(meanHorizontalDelta) < 0.20f;
            if (mostlyUp && littleTraverse && horizontalSpread < 0.18f && verticalSpread < 0.12f)
            {
                ladderLikeStages++;
            }
        }

        float firstHeight = stages[0].Values.Average(position => Vector3.Dot(position, frame.UpAlongSurface));
        float lastHeight = stages[^1].Values.Average(position => Vector3.Dot(position, frame.UpAlongSurface));
        return new ClimbRouteGeometrySummary(
            stages.Count,
            lastHeight - firstHeight,
            horizontalTravel.Values.Average(),
            directionChanges,
            asymmetricStages,
            stages.Count > 1 ? ladderLikeStages / (float)(stages.Count - 1) : 0f,
            maximumMove,
            new ReadOnlyDictionary<ClimbLimb, float>(horizontalTravel));
    }

    private static Vector3 Average(IEnumerable<Vector3> positions)
    {
        Vector3 sum = Vector3.Zero;
        int count = 0;
        foreach (Vector3 position in positions)
        {
            sum += position;
            count++;
        }

        return count > 0 ? sum / count : Vector3.Zero;
    }
}