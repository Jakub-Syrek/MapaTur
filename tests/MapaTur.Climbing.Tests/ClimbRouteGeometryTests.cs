using System.Numerics;

using MapaTur.Climbing;

namespace MapaTur.Climbing.Tests;

public sealed class ClimbRouteGeometryTests
{
    [Fact]
    public void Repeated_vertical_rungs_should_be_classified_as_a_ladder()
    {
        IReadOnlyList<IReadOnlyDictionary<ClimbLimb, Vector3>> stages =
        [
            Stage(0f, 0f, 0f, 0f),
            Stage(0f, 0f, 0.40f, 0.40f),
            Stage(0f, 0f, 0.80f, 0.80f),
            Stage(0f, 0f, 1.20f, 1.20f)
        ];

        ClimbRouteGeometrySummary result = ClimbRouteGeometry.Analyze(
            stages,
            new Vector3(0f, -9.81f, 0f),
            Vector3.UnitZ);

        Assert.Equal(1f, result.LadderLikeStageFraction, 3);
        Assert.False(result.ExercisesNonLadderMovement);
    }

    [Fact]
    public void Traverse_with_asymmetric_contacts_should_exercise_non_ladder_movement()
    {
        IReadOnlyList<IReadOnlyDictionary<ClimbLimb, Vector3>> stages =
        [
            Stage(-1.4f, 0f, 0f, 0.20f),
            Stage(-0.7f, 0.10f, 0.18f, 0.45f),
            Stage(0.1f, 0.18f, 0.40f, 0.28f),
            Stage(0.8f, 0.28f, 0.55f, 0.72f)
        ];

        ClimbRouteGeometrySummary result = ClimbRouteGeometry.Analyze(
            stages,
            new Vector3(0f, -9.81f, 0f),
            Vector3.UnitZ);

        Assert.True(result.ExercisesNonLadderMovement);
        Assert.Equal(0f, result.LadderLikeStageFraction, 3);
        Assert.True(result.MeanHorizontalTravelPerLimbMeters > 1.8f);
        Assert.True(result.AsymmetricStages >= 2);
    }

    [Fact]
    public void Zig_zag_should_count_horizontal_direction_changes()
    {
        IReadOnlyList<IReadOnlyDictionary<ClimbLimb, Vector3>> stages =
        [
            Stage(-0.7f, 0f, 0f, 0.20f),
            Stage(0.2f, 0.20f, 0.35f, 0.55f),
            Stage(-0.8f, 0.40f, 0.70f, 0.50f),
            Stage(0.3f, 0.62f, 0.92f, 1.10f)
        ];

        ClimbRouteGeometrySummary result = ClimbRouteGeometry.Analyze(
            stages,
            new Vector3(0f, -9.81f, 0f),
            Vector3.UnitZ);

        Assert.True(result.HorizontalDirectionChanges >= 2);
        Assert.True(result.ExercisesNonLadderMovement);
    }

    private static Dictionary<ClimbLimb, Vector3> Stage(
        float horizontalOffset,
        float verticalOffset,
        float leftAsymmetry,
        float rightAsymmetry) => new Dictionary<ClimbLimb, Vector3>
        {
            [ClimbLimb.LeftHand] = new(horizontalOffset - 0.75f, 2.8f + verticalOffset + leftAsymmetry, 0f),
            [ClimbLimb.RightHand] = new(horizontalOffset + 0.75f, 2.8f + verticalOffset + rightAsymmetry, 0f),
            [ClimbLimb.LeftFoot] = new(horizontalOffset - 0.48f, 1.2f + verticalOffset + rightAsymmetry, 0f),
            [ClimbLimb.RightFoot] = new(horizontalOffset + 0.48f, 1.2f + verticalOffset + leftAsymmetry, 0f)
        };
}