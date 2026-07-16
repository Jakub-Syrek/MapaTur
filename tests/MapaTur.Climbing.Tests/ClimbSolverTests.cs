using System.Numerics;

using MapaTur.Climbing;

namespace MapaTur.Climbing.Tests;

public sealed class ClimbSolverTests
{
    [Fact]
    public void TryMove_should_reject_hold_outside_limb_reach()
    {
        ClimbState state = CreateState();
        ClimbSolver solver = new();
        ClimbHold distantHold = Hold("distant", 0f, 6f);

        ClimbMoveResult result = solver.TryMove(state, ClimbLimb.LeftHand, distantHold);

        Assert.False(result.Succeeded);
        Assert.Same(state, result.State);
        Assert.Contains("cannot reach", result.FailureReason);
    }

    [Fact]
    public void Assess_should_assign_more_load_to_feet_when_holds_are_equal()
    {
        ClimbState state = CreateState();
        ClimbSolver solver = new();

        StabilityAssessment assessment = solver.Assess(state);

        Assert.True(assessment.Loads[ClimbLimb.LeftFoot] > assessment.Loads[ClimbLimb.LeftHand]);
        Assert.True(assessment.Loads[ClimbLimb.RightFoot] > assessment.Loads[ClimbLimb.RightHand]);
    }

    [Fact]
    public void TryMove_should_update_contact_and_increase_fatigue()
    {
        ClimbState state = CreateState();
        ClimbSolver solver = new();
        ClimbHold nextHold = Hold("next-left-hand", -0.95f, 3.7f, 0.85f);

        ClimbMoveResult result = solver.TryMove(state, ClimbLimb.LeftHand, nextHold);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(nextHold.Id, result.State.Contacts[ClimbLimb.LeftHand].Hold.Id);
        Assert.True(result.State.Contacts[ClimbLimb.LeftHand].Fatigue > state.Contacts[ClimbLimb.LeftHand].Fatigue);
    }

    [Fact]
    public void TryMove_should_transfer_hips_before_sparse_hand_reach_check()
    {
        ClimbState state = ClimbState.Create(
            new Vector3(-0.1117f, 2.5159f, 0.6051f),
            [
                new LimbContact(ClimbLimb.LeftHand, Hold("hand-left-1", -0.78f, 3.51f), 0f),
                new LimbContact(ClimbLimb.RightHand, Hold("hand-right-1", 0.87f, 3.41f), 0f),
                new LimbContact(ClimbLimb.LeftFoot, Hold("foot-left-1", -0.60f, 1.98f), 0f),
                new LimbContact(ClimbLimb.RightFoot, Hold("foot-right-1", 0.40f, 1.83f), 0f)
            ]);

        ClimbMoveResult result = new ClimbSolver().TryMove(
            state,
            ClimbLimb.LeftHand,
            Hold("hand-left-2", -1.30f, 4.15f));

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.True(result.State.Pelvis.Y > state.Pelvis.Y);
    }

    [Fact]
    public void TryMove_should_return_failure_when_contact_band_cannot_fit_a_pelvis()
    {
        ClimbState state = CreateState();
        ClimbSolver solver = new();
        ClimbHold highFootHold = Hold("high-right-foot", 0.5f, 3.7f);

        ClimbMoveResult result = solver.TryMove(state, ClimbLimb.RightFoot, highFootHold);

        Assert.False(result.Succeeded);
        Assert.Same(state, result.State);
        Assert.Contains("pelvis", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FindHolds_should_return_only_holds_inside_search_radius()
    {
        ClimbHold near = Hold("near", 0f, 2f);
        ClimbHold far = Hold("far", 0f, 5f);
        InMemoryClimbSurface surface = new([near, far]);

        IReadOnlyList<ClimbHold> found = surface.FindHolds(new Vector3(0f, 2f, 0f), 1f);

        Assert.Collection(found, hold => Assert.Equal("near", hold.Id));
    }

    [Fact]
    public void TryMove_should_allow_a_hand_to_crimp_a_foot_edge_with_penalty()
    {
        ClimbState state = CreateState();
        ClimbHold footEdge = new("foot-edge", new Vector3(-0.7f, 3.2f, 0f), Vector3.UnitZ, 0.9f, ClimbHoldType.FootEdge);

        ClimbMoveResult result = new ClimbSolver().TryMove(state, ClimbLimb.LeftHand, footEdge);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(ClimbHoldType.FootEdge, result.State.Contacts[ClimbLimb.LeftHand].Hold.Type);
        Assert.True(result.State.Contacts[ClimbLimb.LeftHand].Fatigue > 0.05f);
        Assert.True(footEdge.LoadMultiplier(ClimbLimb.LeftHand) < footEdge.LoadMultiplier(ClimbLimb.LeftFoot));
    }

    [Fact]
    public void TryMove_should_not_infer_limb_ownership_from_the_hold_id_or_side()
    {
        ClimbState state = CreateState();
        ClimbHold nominallyLeftHold = Hold("hand-left-looking-id", 0.55f, 3.25f);

        ClimbMoveResult result = new ClimbSolver().TryMove(
            state,
            ClimbLimb.RightHand,
            nominallyLeftHold);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(nominallyLeftHold.Id, result.State.Contacts[ClimbLimb.RightHand].Hold.Id);
    }

    [Fact]
    public void TryMove_should_allow_both_hands_to_match_on_a_wide_jug()
    {
        ClimbHold sharedJug = new(
            "neutral-shared-jug",
            new Vector3(0f, 3.2f, 0f),
            Vector3.UnitZ,
            0.95f,
            ClimbHoldType.Jug,
            usableWidthMeters: 0.46f);
        ClimbState state = ClimbState.Create(
            new Vector3(0f, 2.3f, 0.65f),
            [
                new LimbContact(ClimbLimb.LeftHand, Hold("previous-left", -0.8f, 3.2f), 0f),
                new LimbContact(ClimbLimb.RightHand, sharedJug, 0f),
                new LimbContact(ClimbLimb.LeftFoot, Hold("lower-a", -0.7f, 1.4f), 0f),
                new LimbContact(ClimbLimb.RightFoot, Hold("lower-b", 0.7f, 1.5f), 0f)
            ]);

        ClimbMoveResult result = new ClimbSolver().TryMove(state, ClimbLimb.LeftHand, sharedJug);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(sharedJug.Id, result.State.Contacts[ClimbLimb.LeftHand].Hold.Id);
        Assert.Equal(sharedJug.Id, result.State.Contacts[ClimbLimb.RightHand].Hold.Id);
        Vector3 leftTarget = result.State.GetContactTarget(ClimbLimb.LeftHand, new Vector3(0f, -9.81f, 0f));
        Vector3 rightTarget = result.State.GetContactTarget(ClimbLimb.RightHand, new Vector3(0f, -9.81f, 0f));
        Assert.True(Vector3.Distance(leftTarget, rightTarget) >= 0.11f);
        Assert.True(leftTarget.X < rightTarget.X);
    }

    [Fact]
    public void TryMove_should_reject_a_two_hand_match_when_the_hold_has_one_slot()
    {
        ClimbHold pocket = new(
            "single-pocket",
            new Vector3(0f, 3.2f, 0f),
            Vector3.UnitZ,
            0.9f,
            ClimbHoldType.Pocket);
        ClimbState state = ClimbState.Create(
            new Vector3(0f, 2.3f, 0.65f),
            [
                new LimbContact(ClimbLimb.LeftHand, Hold("previous-left", -0.8f, 3.2f), 0f),
                new LimbContact(ClimbLimb.RightHand, pocket, 0f),
                new LimbContact(ClimbLimb.LeftFoot, Hold("lower-a", -0.7f, 1.4f), 0f),
                new LimbContact(ClimbLimb.RightFoot, Hold("lower-b", 0.7f, 1.5f), 0f)
            ]);

        ClimbMoveResult result = new ClimbSolver().TryMove(state, ClimbLimb.LeftHand, pocket);

        Assert.False(result.Succeeded);
        Assert.Contains("no free contact slot", result.FailureReason);
    }

    [Fact]
    public void Crimp_should_add_more_hand_fatigue_than_a_jug()
    {
        ClimbState state = CreateState();
        ClimbHold crimp = new("crimp", new Vector3(-0.82f, 3.25f, 0f), Vector3.UnitZ, 0.9f, ClimbHoldType.Crimp);

        ClimbMoveResult result = new ClimbSolver().TryMove(state, ClimbLimb.LeftHand, crimp);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(ClimbHoldType.Crimp, result.State.Contacts[ClimbLimb.LeftHand].Hold.Type);
        Assert.True(result.State.Contacts[ClimbLimb.LeftHand].Fatigue > 0.04f);
    }

    [Fact]
    public void Foot_contact_point_should_be_above_the_foothold()
    {
        ClimbHold footEdge = new(
            "foot-edge",
            new Vector3(0f, 1.5f, 0f),
            Vector3.UnitZ,
            0.9f,
            ClimbHoldType.FootEdge);

        Vector3 footPoint = footEdge.ContactPointFor(ClimbLimb.LeftFoot);

        Assert.Equal(footEdge.FootWallClearanceMeters, footPoint.Z - footEdge.ContactPoint.Z, 5);
        Assert.Equal(footEdge.FootContactLiftMeters, footPoint.Y - footEdge.ContactPoint.Y, 5);
        Assert.True(footPoint.Y > footEdge.Position.Y);
    }

    [Fact]
    public void Micro_edge_ankle_target_should_clear_the_hold_and_shoe_depth()
    {
        ClimbHold microEdge = new(
            "micro-edge",
            new Vector3(0f, 1.5f, 0f),
            Vector3.UnitZ,
            0.9f,
            ClimbHoldType.FootEdge);

        Vector3 ankleTarget = microEdge.ContactPointFor(ClimbLimb.LeftFoot);
        float wallNormalDistance = Vector3.Dot(ankleTarget - microEdge.Position, microEdge.Normal);

        Assert.InRange(wallNormalDistance, 0.215f, 0.225f);
    }

    private static ClimbState CreateState() => ClimbState.Create(
        new Vector3(0f, 2.3f, 0.65f),
        [
            new LimbContact(ClimbLimb.LeftHand, Hold("left-hand", -0.8f, 3.2f), 0f),
            new LimbContact(ClimbLimb.RightHand, Hold("right-hand", 0.8f, 3.1f), 0f),
            new LimbContact(ClimbLimb.LeftFoot, Hold("left-foot", -0.7f, 1.4f), 0f),
            new LimbContact(ClimbLimb.RightFoot, Hold("right-foot", 0.7f, 1.5f), 0f)
        ]);

    private static ClimbHold Hold(string id, float x, float y, float quality = 1f) =>
        new(id, new Vector3(x, y, 0f), Vector3.UnitZ, quality);
}