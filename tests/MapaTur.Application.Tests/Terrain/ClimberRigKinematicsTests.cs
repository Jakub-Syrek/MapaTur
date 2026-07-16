using System.Numerics;

using MapaTur.Application.Terrain;
using MapaTur.Climbing;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Etap 3 gate: the hiker rig reaches four real contacts through the climbing whole-body seam —
/// no inverted elbows/knees, pelvis where the solver put it, body outside the rock.
/// All climb-space inputs are Z-up real metres with gravity (0, 0, -9.81).
/// </summary>
public sealed class ClimberRigKinematicsTests
{
    private static readonly string HikerPath =
        Path.Combine(AppContext.BaseDirectory, "TestData", "hiker.glb");

    private static SkinnedModel LoadHiker() => SkinnedModel.Load(HikerPath);

    /// <summary>Wall in the XZ plane at y=0 facing +Y (north, toward the climber standing at y &gt; 0).</summary>
    private static TrianglePatchClimbSurface NorthFacingWall()
    {
        Vector3[] vertices = [new(-3f, 0f, -1f), new(3f, 0f, -1f), new(3f, 0f, 4f), new(-3f, 0f, 4f)];
        int[] indices = [0, 3, 1, 1, 3, 2];
        return new TrianglePatchClimbSurface(new ClimbSurfacePatch("test-wall", vertices, indices, []));
    }

    /// <summary>
    /// Hold placement derived from the rig's MEASURED proportions (the stylized hiker has far shorter
    /// limbs than a human of its height), so every target is genuinely reachable. Facing -Y at yaw 0,
    /// the character's left is +X.
    /// </summary>
    private static Dictionary<ClimbLimb, LimbContact> FourContacts(ClimberRigKinematics adapter, Vector3 pelvis)
    {
        Vector3 outward = new(0f, 1f, 0f);
        Vector3 leftShoulder = pelvis + adapter.LeftShoulderOffsetMeters;
        Vector3 leftHip = pelvis + adapter.LeftHipOffsetMeters;
        float handUp = 0.50f * adapter.ArmReachMeters;
        float footDown = 0.55f * adapter.LegReachMeters;

        ClimbHold Hand(string id, float sideSign) => new(
            id,
            new Vector3(sideSign * (leftShoulder.X + 0.05f), 0f, leftShoulder.Z + handUp),
            outward,
            0.9f);
        ClimbHold Foot(string id, float sideSign) => new(
            id,
            new Vector3(sideSign * (leftHip.X + 0.03f), 0f, leftHip.Z - footDown),
            outward,
            0.9f,
            ClimbHoldType.FootEdge);

        return new Dictionary<ClimbLimb, LimbContact>
        {
            [ClimbLimb.LeftHand] = new(ClimbLimb.LeftHand, Hand("lh", 1f), 0f),
            [ClimbLimb.RightHand] = new(ClimbLimb.RightHand, Hand("rh", -1f), 0f),
            [ClimbLimb.LeftFoot] = new(ClimbLimb.LeftFoot, Foot("lf", 1f), 0f),
            [ClimbLimb.RightFoot] = new(ClimbLimb.RightFoot, Foot("rf", -1f), 0f)
        };
    }

    private static ClimberRigKinematics CreateAdapter(SkinnedModel model)
    {
        var adapter = new ClimberRigKinematics(model, ClimberRigProfile.CreateDefault(), 1.7f);
        adapter.SetClimbContext(NorthFacingWall(), FourContacts(adapter, SeedPose().Pelvis), ClimbWorld.Gravity);
        return adapter;
    }

    /// <summary>Pelvis ~0.35 m off the wall; at yaw 0 the model already faces -Y (the wall).</summary>
    private static ClimbWholeBodyRootPose SeedPose() => new(new Vector3(0f, 0.36f, 0.95f), 0f, 0f, 0f);

    [Fact]
    public void Evaluate_should_place_pelvis_at_root_pose()
    {
        ClimberRigKinematics adapter = CreateAdapter(LoadHiker());

        ClimbWholeBodyPoseSample sample = adapter.Evaluate(SeedPose());

        Assert.True(
            Vector3.Distance(sample.PoseLandmarks.Pelvis, SeedPose().Pelvis) < 0.05f,
            $"pelvis landmark {sample.PoseLandmarks.Pelvis} should sit at root {SeedPose().Pelvis}");
    }

    [Fact]
    public void Evaluate_should_reach_all_four_contacts()
    {
        ClimberRigKinematics adapter = CreateAdapter(LoadHiker());

        ClimbWholeBodyPoseSample sample = adapter.Evaluate(SeedPose());

        Assert.Equal(4, sample.Contacts.Count);
        foreach (ClimbWholeBodyContactState contact in sample.Contacts)
        {
            float error = Vector3.Distance(contact.ActualPosition, contact.TargetPosition);
            Assert.True(
                error <= 0.10f,
                $"{contact.Limb} missed its hold by {error:F3} m (target {contact.TargetPosition}, actual {contact.ActualPosition})");
        }
    }

    [Fact]
    public void Evaluate_should_not_invert_elbows_or_knees()
    {
        ClimberRigKinematics adapter = CreateAdapter(LoadHiker());

        ClimbWholeBodyPoseSample sample = adapter.Evaluate(SeedPose());
        SmplxPoseLandmarks lm = sample.PoseLandmarks;

        // Elbows bend below/outside the shoulder-wrist chord, never above it (reversed elbow).
        AssertBendsToward(lm.LeftShoulder, lm.LeftElbow, lm.LeftWrist, -Vector3.UnitZ, "left elbow");
        AssertBendsToward(lm.RightShoulder, lm.RightElbow, lm.RightWrist, -Vector3.UnitZ, "right elbow");

        // Knees bend toward the wall (climber faces -Y), never away from it (reversed knee).
        AssertBendsToward(lm.LeftHip, lm.LeftKnee, lm.LeftAnkle, -Vector3.UnitY, "left knee");
        AssertBendsToward(lm.RightHip, lm.RightKnee, lm.RightAnkle, -Vector3.UnitY, "right knee");
    }

    [Fact]
    public void Evaluate_should_keep_body_out_of_the_wall()
    {
        ClimberRigKinematics adapter = CreateAdapter(LoadHiker());

        ClimbWholeBodyPoseSample sample = adapter.Evaluate(SeedPose());

        Assert.True(
            sample.MinimumBodyClearanceMeters >= 0.015f,
            $"torso/head must stay outside the rock, clearance {sample.MinimumBodyClearanceMeters:F3} m");
    }

    [Fact]
    public void Evaluate_should_be_deterministic()
    {
        ClimberRigKinematics adapter = CreateAdapter(LoadHiker());

        ClimbWholeBodyPoseSample first = adapter.Evaluate(SeedPose());
        ClimbWholeBodyPoseSample second = adapter.Evaluate(SeedPose());

        Assert.Equal(first.CenterOfMass, second.CenterOfMass);
        Assert.Equal(first.MinimumBodyClearanceMeters, second.MinimumBodyClearanceMeters);
        for (int i = 0; i < first.Contacts.Count; i++)
        {
            Assert.Equal(first.Contacts[i].ActualPosition, second.Contacts[i].ActualPosition);
        }
    }

    [Fact]
    public void WholeBodySolver_should_settle_a_feasible_pose_on_the_hiker()
    {
        ClimberRigKinematics adapter = CreateAdapter(LoadHiker());
        var solver = new SequentialWholeBodyClimbSolver(
            SmplxPosePriorProfile.CreateBootstrap(),
            new ClimbMechanicsConfiguration { Gravity = ClimbWorld.Gravity });
        ClimbSurfaceFrame frame = NorthFacingWall().SampleSurface(new Vector3(0f, 0.4f, 0.9f), ClimbWorld.Gravity);
        var request = new ClimbWholeBodySolveRequest
        {
            ReferencePose = SeedPose(),
            SeedPose = SeedPose(),
            SurfaceFrame = frame,
            CharacteristicLengthMeters = 1.7f,
            MaximumContactErrorMeters = 0.10f
        };

        ClimbWholeBodySolveResult result = solver.Solve(request, adapter);

        Assert.True(result.IsFeasible, $"whole-body solve infeasible: cost {result.Cost}");
        Assert.True(
            result.MaximumContactErrorMeters <= 0.10f,
            $"max contact error {result.MaximumContactErrorMeters:F3} m");
        Assert.True(
            result.Pose.MinimumBodyClearanceMeters >= 0.015f,
            $"solved pose clips the wall: {result.Pose.MinimumBodyClearanceMeters:F3} m");
    }

    private static void AssertBendsToward(Vector3 root, Vector3 joint, Vector3 end, Vector3 expectedSide, string label)
    {
        Vector3 chordMidpoint = (root + end) * 0.5f;
        Vector3 bend = joint - chordMidpoint;
        if (bend.Length() < 0.015f)
        {
            return; // effectively straight limb - no inversion to detect
        }

        float side = Vector3.Dot(Vector3.Normalize(bend), expectedSide);
        Assert.True(side > -0.15f, $"{label} bends {bend} against expected side {expectedSide} (dot {side:F2})");
    }
}