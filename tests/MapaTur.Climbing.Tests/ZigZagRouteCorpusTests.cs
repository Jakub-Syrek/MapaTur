using System.Numerics;

using MapaTur.Climbing;

namespace MapaTur.Climbing.Tests;

public sealed class ZigZagRouteCorpusTests
{
    private const float VerticalWall = 0f;
    private const float OverhangWall = 24f;

    [Theory]
    [InlineData(VerticalWall)]
    [InlineData(OverhangWall)]
    public void Corpus_should_have_28_moves(float wallPitchDegrees)
    {
        ZigZagRouteCorpus corpus = ZigZagRouteCorpus.Create(wallPitchDegrees);

        Assert.Equal(28, corpus.Moves.Count);
    }

    [Theory]
    [InlineData(VerticalWall)]
    [InlineData(OverhangWall)]
    public void Corpus_should_apply_all_moves_without_rejection_or_fall(float wallPitchDegrees)
    {
        ZigZagRouteCorpus corpus = ZigZagRouteCorpus.Create(wallPitchDegrees);
        ClimbSolver solver = CreateSolver();
        ClimbState state = corpus.InitialState;

        List<string> failures = [];
        for (int index = 0; index < corpus.Moves.Count; index++)
        {
            CorpusMove move = corpus.Moves[index];
            ClimbMoveResult result = solver.TryMove(state, move.Limb, corpus.HoldsById[move.HoldId]);
            if (!result.Succeeded || result.State.HasFallen)
            {
                failures.Add($"move {index + 1}/{corpus.Moves.Count} {move.Limb} -> {move.HoldId}: {result.FailureReason}");
            }

            state = result.State;
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Theory]
    [InlineData(VerticalWall)]
    [InlineData(OverhangWall)]
    public void Corpus_should_gain_height_along_gravity_up(float wallPitchDegrees)
    {
        ZigZagRouteCorpus corpus = ZigZagRouteCorpus.Create(wallPitchDegrees);
        ClimbSolver solver = CreateSolver();
        ClimbState state = corpus.InitialState;

        foreach (CorpusMove move in corpus.Moves)
        {
            state = solver.TryMove(state, move.Limb, corpus.HoldsById[move.HoldId]).State;
        }

        Assert.True(
            state.Pelvis.Z > corpus.InitialState.Pelvis.Z + 2.0f,
            $"pelvis rose only {state.Pelvis.Z - corpus.InitialState.Pelvis.Z:F2} m (from {corpus.InitialState.Pelvis.Z:F2} to {state.Pelvis.Z:F2})");
    }

    [Fact]
    public void Corpus_route_should_exercise_non_ladder_movement()
    {
        ZigZagRouteCorpus corpus = ZigZagRouteCorpus.Create(OverhangWall);

        Assert.True(corpus.Geometry.ExercisesNonLadderMovement);
    }

    [Fact]
    public void Wide_jug_should_accept_second_hand_in_z_up()
    {
        ClimbHold wideJug = new(
            "shared-jug",
            new Vector3(-0.1f, 0f, 1.55f),
            new Vector3(0f, -1f, 0f),
            0.95f,
            ClimbHoldType.Jug,
            usableWidthMeters: 0.6f);
        ClimbSolver solver = CreateSolver();
        ClimbState state = FourContactState(wideJug);

        ClimbMoveResult result = solver.TryMove(state, ClimbLimb.RightHand, wideJug);

        Assert.True(result.Succeeded, result.FailureReason);
    }

    [Fact]
    public void Pocket_should_reject_second_hand_in_z_up()
    {
        ClimbHold pocket = new(
            "single-pocket",
            new Vector3(-0.1f, 0f, 1.55f),
            new Vector3(0f, -1f, 0f),
            0.95f,
            ClimbHoldType.Pocket);
        ClimbSolver solver = CreateSolver();
        ClimbState state = FourContactState(pocket);

        ClimbMoveResult result = solver.TryMove(state, ClimbLimb.RightHand, pocket);

        Assert.False(result.Succeeded);
    }

    /// <summary>Vertical wall facing south (normal -Y) in Z-up axes; left hand starts on the given hold.</summary>
    private static ClimbState FourContactState(ClimbHold leftHandHold)
    {
        Vector3 wallNormal = new(0f, -1f, 0f);
        ClimbHold rightHand = new("start-right-hand", new Vector3(0.5f, 0f, 1.5f), wallNormal, 0.9f);
        ClimbHold leftFoot = new("start-left-foot", new Vector3(-0.3f, 0f, 0f), wallNormal, 0.9f, ClimbHoldType.FootEdge);
        ClimbHold rightFoot = new("start-right-foot", new Vector3(0.3f, 0f, 0f), wallNormal, 0.9f, ClimbHoldType.FootEdge);
        return ClimbState.Create(
            new Vector3(0f, -0.58f, 0.85f),
            [
                new LimbContact(ClimbLimb.LeftHand, leftHandHold, 0f),
                new LimbContact(ClimbLimb.RightHand, rightHand, 0f),
                new LimbContact(ClimbLimb.LeftFoot, leftFoot, 0f),
                new LimbContact(ClimbLimb.RightFoot, rightFoot, 0f)
            ]);
    }

    private static ClimbSolver CreateSolver() =>
        new(null, new ClimbMechanicsConfiguration { Gravity = ClimbWorld.Gravity });
}