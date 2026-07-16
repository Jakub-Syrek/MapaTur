using System.Numerics;
using System.Text.Json;

using MapaTur.Climbing;

namespace MapaTur.Climbing.Tests;

public sealed class ClimbMechanicsTests
{
    private const float DegreesToRadians = MathF.PI / 180f;

    [Fact]
    public void Surface_frame_should_follow_gravity_in_y_up_world()
    {
        float angle = 24f * DegreesToRadians;
        Vector3 normal = new(0f, -MathF.Sin(angle), MathF.Cos(angle));

        ClimbSurfaceFrame frame = ClimbSurfaceFrame.Create(Vector3.Zero, normal, new Vector3(0f, -9.81f, 0f));

        Assert.InRange(Vector3.Dot(frame.UpAlongSurface, new Vector3(0f, MathF.Cos(angle), MathF.Sin(angle))), 0.999f, 1.001f);
    }

    [Fact]
    public void Overhang_should_expose_expected_outward_gravity_component()
    {
        float angle = 24f * DegreesToRadians;
        ClimbMechanicsSolver solver = CreateSolver(new Vector3(0f, -9.81f, 0f));

        ClimbEquilibriumResult result = solver.Solve(
            Body(new Vector3(0f, 0f, 0.65f)),
            Contacts(angle));

        Assert.InRange(result.GravityNormalFraction, MathF.Sin(angle) - 0.002f, MathF.Sin(angle) + 0.002f);
    }

    [Fact]
    public void Overhang_should_drop_the_center_of_mass_under_the_hands()
    {
        ClimbMechanicsSolver solver = CreateSolver(new Vector3(0f, -9.81f, 0f));
        ClimbEquilibriumResult vertical = solver.Solve(Body(new Vector3(0f, 0f, 0.65f)), Contacts(0f));
        ClimbEquilibriumResult overhang = solver.Solve(Body(new Vector3(0f, 0f, 0.65f)), Contacts(24f * DegreesToRadians));

        Assert.InRange(vertical.RootPose.GravityDropMeters, 0f, 0.001f);
        Assert.True(overhang.RootPose.GravityDropMeters > vertical.RootPose.GravityDropMeters + 0.10f,
            $"Expected the overhang to lower the mass: vertical={vertical.RootPose.GravityDropMeters:F3} m, " +
            $"overhang={overhang.RootPose.GravityDropMeters:F3} m; " +
            $"overhang forces={Describe(overhang)}.");
    }

    [Fact]
    public void Foot_contact_should_never_pull_into_the_wall_without_a_hook()
    {
        float angle = 24f * DegreesToRadians;
        ClimbMechanicsSolver solver = CreateSolver(new Vector3(0f, -9.81f, 0f));
        ClimbEquilibriumResult result = solver.Solve(Body(new Vector3(0f, 0f, 0.65f)), Contacts(angle));
        ClimbContactForce leftFoot = result.ContactForces[ClimbLimb.LeftFoot];

        Assert.True(leftFoot.NormalForceNewtons >= -0.001f, $"Foot pulled into the wall with {leftFoot.NormalForceNewtons:F3} N.");
    }

    [Fact]
    public void Root_pose_should_not_copy_the_overhang_angle()
    {
        float angle = 24f * DegreesToRadians;
        ClimbMechanicsSolver solver = CreateSolver(new Vector3(0f, -9.81f, 0f));
        ClimbEquilibriumResult result = solver.Solve(Body(new Vector3(0f, 0f, 0.65f)), Contacts(angle));

        Assert.InRange(MathF.Abs(result.RootPose.SurfacePitchRadians), 0f, angle * 0.70f);
    }

    [Fact]
    public void Mechanics_should_support_mapatur_z_up_world()
    {
        ClimbMechanicsSolver solver = CreateSolver(new Vector3(0f, 0f, -9.81f));
        // MapaTur uses Z-up; the surface normal points from the cliff toward the body at positive Y.
        Vector3 normal = new(0f, 1f, 0f);
        ClimbMechanicsContact[] contacts =
        [
            Contact(ClimbLimb.LeftHand, new Vector3(-0.6f, 0f, 1f), normal),
            Contact(ClimbLimb.RightHand, new Vector3(0.6f, 0f, 1f), normal),
            Contact(ClimbLimb.LeftFoot, new Vector3(-0.5f, 0f, -1f), normal),
            Contact(ClimbLimb.RightFoot, new Vector3(0.5f, 0f, -1f), normal)
        ];

        ClimbEquilibriumResult result = solver.Solve(Body(new Vector3(0f, 0.65f, 0f)), contacts);

        Assert.InRange(result.ForceResidualFraction, 0f, 0.08f);
    }

    [Fact]
    public void Mapatur_foot_target_should_lift_along_z_up_surface_frame()
    {
        ClimbHold hold = new(
            "z-up-edge",
            new Vector3(2f, 3f, 4f),
            Vector3.UnitY,
            0.9f,
            ClimbHoldType.FootEdge);

        Vector3 target = hold.ContactPointFor(ClimbLimb.LeftFoot, new Vector3(0f, 0f, -9.81f));

        Assert.Equal(hold.FootContactLiftMeters, target.Z - hold.ContactPoint.Z, 5);
        Assert.Equal(hold.FootWallClearanceMeters, target.Y - hold.ContactPoint.Y, 5);
    }

    [Fact]
    public void Surface_adapter_should_return_gravity_relative_frame()
    {
        InMemoryClimbSurface surface = new([], Vector3.UnitY);

        ClimbSurfaceFrame frame = surface.SampleSurface(new Vector3(2f, 3f, 4f), new Vector3(0f, 0f, -9.81f));

        Assert.Equal(new Vector3(2f, 0f, 4f), frame.Position);
        Assert.Equal(Vector3.UnitZ, frame.UpAlongSurface);
    }

    [Fact]
    public void Foot_swing_should_leave_and_return_to_exact_contacts()
    {
        Vector3 normal = Vector3.UnitZ;
        Vector3 gravity = new(0f, -9.81f, 0f);

        Assert.Equal(Vector3.Zero, ClimbContactTrajectory.FootSwingOffset(0f, normal, gravity));
        Assert.InRange(ClimbContactTrajectory.FootSwingOffset(1f, normal, gravity).Length(), 0f, 1e-5f);
    }

    [Fact]
    public void Foot_swing_midpoint_should_clear_the_wall_and_lift_the_shoe()
    {
        Vector3 offset = ClimbContactTrajectory.FootSwingOffset(
            0.5f,
            Vector3.UnitZ,
            new Vector3(0f, -9.81f, 0f));

        Assert.InRange(offset.Z, 0.139f, 0.141f);
        Assert.InRange(offset.Y, 0.099f, 0.101f);
    }

    [Fact]
    public void Hanging_root_should_drop_until_planted_arms_are_nearly_straight()
    {
        ClimbReachConstraint[] constraints =
        [
            new(ClimbLimb.LeftHand, new Vector3(-0.3f, 0f, 0f), new Vector3(-0.3f, 1f, 0f), 1.25f),
            new(ClimbLimb.RightHand, new Vector3(0.3f, 0f, 0f), new Vector3(0.3f, 1f, 0f), 1.25f)
        ];

        ClimbHangingRootResult result = ClimbHangingRootSolver.RecommendAdditionalDrop(
            constraints,
            new Vector3(0f, -9.81f, 0f));

        Assert.InRange(result.AdditionalGravityDropMeters, 0.230f, 0.232f);
        Assert.InRange(result.TargetArmExtensionRatio, 0.984f, 0.986f);
    }

    [Fact]
    public void Hanging_root_should_work_in_mapatur_z_up_world()
    {
        ClimbReachConstraint[] constraints =
        [
            new(ClimbLimb.LeftHand, Vector3.Zero, new Vector3(0f, 0f, 1f), 1.25f),
            new(ClimbLimb.RightHand, new Vector3(0.4f, 0f, 0f), new Vector3(0.4f, 0f, 1f), 1.25f)
        ];

        ClimbHangingRootResult result = ClimbHangingRootSolver.RecommendAdditionalDrop(
            constraints,
            new Vector3(0f, 0f, -9.81f));

        Assert.InRange(result.AdditionalGravityDropMeters, 0.230f, 0.232f);
    }

    [Fact]
    public void Hanging_root_should_respect_a_planted_limb_reach_limit()
    {
        ClimbReachConstraint[] constraints =
        [
            new(ClimbLimb.LeftHand, new Vector3(-0.3f, 0f, 0f), new Vector3(-0.3f, 0.8f, 0f), 1.25f),
            new(ClimbLimb.RightHand, new Vector3(0.3f, 0f, 0f), new Vector3(0.3f, 1.1f, 0f), 1.25f),
            new(ClimbLimb.LeftFoot, new Vector3(-0.2f, 0f, 0f), new Vector3(-0.2f, 0.9f, 0f), 1.0f)
        ];

        ClimbHangingRootResult result = ClimbHangingRootSolver.RecommendAdditionalDrop(
            constraints,
            new Vector3(0f, -9.81f, 0f));

        Assert.InRange(result.AdditionalGravityDropMeters, 0.094f, 0.096f);
    }

    [Fact]
    public void Active_pull_should_allow_more_elbow_flexion_than_passive_hang()
    {
        ClimbReachConstraint[] constraints =
        [
            new(ClimbLimb.LeftHand, Vector3.Zero, Vector3.UnitY, 1.25f)
        ];

        ClimbHangingRootResult passive = ClimbHangingRootSolver.RecommendAdditionalDrop(
            constraints,
            new Vector3(0f, -9.81f, 0f));
        ClimbHangingRootResult active = ClimbHangingRootSolver.RecommendAdditionalDrop(
            constraints,
            new Vector3(0f, -9.81f, 0f),
            targetArmExtensionRatio: 0.88f);

        Assert.True(active.AdditionalGravityDropMeters < passive.AdditionalGravityDropMeters - 0.10f);
    }

    [Fact]
    public void Ankle_should_not_fold_the_toes_back_into_the_shin()
    {
        Vector3 direction = ClimbAnkleOrientation.ConstrainToeDirection(
            knee: Vector3.UnitY,
            ankle: Vector3.Zero,
            neutralToe: Vector3.UnitZ,
            desiredToe: Vector3.UnitY);

        Assert.InRange(AngleDegrees(Vector3.UnitY, direction), 69.9f, 70.1f);
    }

    [Fact]
    public void Ankle_should_limit_plantarflexion_and_rotation_from_neutral()
    {
        Vector3 plantarflexed = ClimbAnkleOrientation.ConstrainToeDirection(
            knee: Vector3.UnitY,
            ankle: Vector3.Zero,
            neutralToe: Vector3.UnitZ,
            desiredToe: -Vector3.UnitY);
        Vector3 twisted = ClimbAnkleOrientation.ConstrainToeDirection(
            knee: Vector3.UnitY,
            ankle: Vector3.Zero,
            neutralToe: Vector3.UnitZ,
            desiredToe: Vector3.UnitX);

        Assert.InRange(AngleDegrees(Vector3.UnitY, plantarflexed), 134.9f, 135.1f);
        Assert.InRange(AngleDegrees(Vector3.UnitZ, twisted), 44.9f, 45.1f);
    }

    [Fact]
    public void Ankle_constraint_should_be_axis_independent_for_mapatur()
    {
        Vector3 direction = ClimbAnkleOrientation.ConstrainToeDirection(
            knee: Vector3.UnitZ,
            ankle: Vector3.Zero,
            neutralToe: Vector3.UnitY,
            desiredToe: Vector3.UnitZ);

        Assert.InRange(AngleDegrees(Vector3.UnitZ, direction), 69.9f, 70.1f);
    }

    [Fact]
    public void Knee_height_guard_should_measure_directly_from_the_hip()
    {
        float above = ClimbJointAnatomy.HeightAboveHip(
            hip: Vector3.Zero,
            knee: new Vector3(0.8f, 0.15f, 0f),
            gravity: new Vector3(0f, -9.81f, 0f));
        float below = ClimbJointAnatomy.HeightAboveHip(
            hip: Vector3.Zero,
            knee: new Vector3(-0.8f, -0.05f, 0f),
            gravity: new Vector3(0f, -9.81f, 0f));

        Assert.Equal(0.15f, above, 5);
        Assert.Equal(-0.05f, below, 5);
    }

    [Fact]
    public void Knee_height_guard_should_support_mapatur_z_up_world()
    {
        float height = ClimbJointAnatomy.HeightAboveHip(
            hip: new Vector3(3f, 4f, 5f),
            knee: new Vector3(3.7f, 4.8f, 5.2f),
            gravity: new Vector3(0f, 0f, -9.81f));

        Assert.Equal(0.20f, height, 5);
    }

    [Fact]
    public void Smplx_bootstrap_should_accept_a_neutral_articulated_pose()
    {
        SmplxPosePriorProfile profile = SmplxPosePriorProfile.CreateBootstrap();

        SmplxPoseAssessment result = profile.Assess(NeutralSmplxPose());

        Assert.True(result.IsInsideHardLimits);
        Assert.Empty(result.HardViolations);
        Assert.Equal(0f, result.MeasurementsDegrees[SmplxPoseFeatureNames.LeftElbowFlexion], 3);
        Assert.Equal(90f, result.MeasurementsDegrees[SmplxPoseFeatureNames.LeftAnkleAngle], 3);
    }

    [Fact]
    public void Smplx_hard_envelope_should_reject_an_overfolded_elbow()
    {
        SmplxPosePriorProfile profile = SmplxPosePriorProfile.CreateBootstrap();
        SmplxPoseLandmarks neutral = NeutralSmplxPose();
        SmplxPoseLandmarks folded = neutral with
        {
            LeftWrist = neutral.LeftElbow + new Vector3(0.05f, 0.40f, 0f)
        };

        SmplxPoseAssessment result = profile.Assess(folded);

        Assert.False(result.IsInsideHardLimits);
        Assert.Contains(result.HardViolations, violation =>
            violation.Feature == SmplxPoseFeatureNames.LeftElbowFlexion
            && violation.ValueDegrees > 150f);
    }

    [Fact]
    public void Smplx_geometry_features_should_be_axis_independent_for_mapatur()
    {
        SmplxPoseLandmarks pose = NeutralSmplxPose();
        Quaternion rotation = Quaternion.CreateFromYawPitchRoll(0.8f, -0.5f, 1.1f);
        SmplxPoseLandmarks rotated = pose with
        {
            Pelvis = Vector3.Transform(pose.Pelvis, rotation),
            Neck = Vector3.Transform(pose.Neck, rotation),
            LeftShoulder = Vector3.Transform(pose.LeftShoulder, rotation),
            LeftElbow = Vector3.Transform(pose.LeftElbow, rotation),
            LeftWrist = Vector3.Transform(pose.LeftWrist, rotation),
            RightShoulder = Vector3.Transform(pose.RightShoulder, rotation),
            RightElbow = Vector3.Transform(pose.RightElbow, rotation),
            RightWrist = Vector3.Transform(pose.RightWrist, rotation),
            LeftHip = Vector3.Transform(pose.LeftHip, rotation),
            LeftKnee = Vector3.Transform(pose.LeftKnee, rotation),
            LeftAnkle = Vector3.Transform(pose.LeftAnkle, rotation),
            LeftFoot = Vector3.Transform(pose.LeftFoot, rotation),
            RightHip = Vector3.Transform(pose.RightHip, rotation),
            RightKnee = Vector3.Transform(pose.RightKnee, rotation),
            RightAnkle = Vector3.Transform(pose.RightAnkle, rotation),
            RightFoot = Vector3.Transform(pose.RightFoot, rotation)
        };

        IReadOnlyDictionary<string, float> expected = SmplxPosePriorProfile.Measure(pose);
        IReadOnlyDictionary<string, float> actual = SmplxPosePriorProfile.Measure(rotated);

        foreach (string feature in SmplxPoseFeatureNames.Required)
        {
            Assert.InRange(MathF.Abs(expected[feature] - actual[feature]), 0f, 0.03f);
        }
    }

    [Fact]
    public void Smplx_export_profile_should_round_trip_through_json()
    {
        SmplxPosePriorProfile expected = SmplxPosePriorProfile.CreateBootstrap();
        string path = Path.Combine(Path.GetTempPath(), $"climber3d-smplx-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(expected));

            SmplxPosePriorProfile actual = SmplxPosePriorProfile.Load(path);

            Assert.Equal(expected.SchemaVersion, actual.SchemaVersion);
            Assert.Equal(expected.SourceKind, actual.SourceKind);
            Assert.Equal(
                expected.GetEnvelope(SmplxPoseFeatureNames.LeftElbowFlexion),
                actual.GetEnvelope(SmplxPoseFeatureNames.LeftElbowFlexion));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Releasing_a_contact_should_change_force_distribution()
    {
        ClimbMechanicsSolver solver = CreateSolver(new Vector3(0f, -9.81f, 0f));
        ClimbMechanicsContact[] contacts = Contacts(24f * DegreesToRadians);
        ClimbEquilibriumResult fourContacts = solver.Solve(Body(new Vector3(0f, 0f, 0.65f)), contacts);
        ClimbEquilibriumResult threeContacts = solver.Solve(
            Body(new Vector3(0f, 0f, 0.65f)),
            contacts.Where(contact => contact.Limb != ClimbLimb.RightHand));

        Assert.True(Vector3.Distance(
                fourContacts.ContactForces[ClimbLimb.LeftHand].ForceNewtons,
                threeContacts.ContactForces[ClimbLimb.LeftHand].ForceNewtons) > 5f);
    }

    [Fact]
    public void Hand_force_should_stay_inside_the_arm_tension_cone()
    {
        ClimbMechanicsSolver solver = CreateSolver(new Vector3(0f, -9.81f, 0f));
        Vector3 normal = Vector3.UnitZ;
        Vector3 leftHand = new(-0.9f, 0.25f, 0f);
        Vector3 leftShoulder = new(-0.25f, 0.10f, 0.55f);
        ClimbMechanicsContact[] contacts =
        [
            new(ClimbLimb.LeftHand, leftHand, normal, 0.9f, ClimbHoldType.Jug, leftShoulder),
            new(ClimbLimb.RightHand, new Vector3(0.9f, 0.25f, 0f), normal, 0.9f, ClimbHoldType.Jug,
                new Vector3(0.25f, 0.10f, 0.55f))
        ];

        ClimbEquilibriumResult result = solver.Solve(Body(new Vector3(0f, 0f, 0.65f)), contacts);
        Vector3 forceDirection = Vector3.Normalize(result.ContactForces[ClimbLimb.LeftHand].ForceNewtons);
        Vector3 armTensionDirection = Vector3.Normalize(leftHand - leftShoulder);
        float angle = MathF.Acos(Math.Clamp(Vector3.Dot(forceDirection, armTensionDirection), -1f, 1f));

        Assert.InRange(angle, 0f, 33f * DegreesToRadians);
    }

    [Fact]
    public void Sideways_hands_should_be_less_feasible_than_hands_above_the_center_of_mass()
    {
        ClimbMechanicsSolver solver = CreateSolver(new Vector3(0f, -9.81f, 0f));
        Vector3 normal = Vector3.UnitZ;
        ClimberBodyMassState body = Body(new Vector3(0f, 0f, 0.65f));
        ClimbMechanicsContact[] sideways =
        [
            new(ClimbLimb.LeftHand, new Vector3(-0.9f, 0.1f, 0f), normal, 0.9f, ClimbHoldType.Jug,
                new Vector3(-0.25f, 0.1f, 0.55f)),
            new(ClimbLimb.RightHand, new Vector3(0.9f, 0.1f, 0f), normal, 0.9f, ClimbHoldType.Jug,
                new Vector3(0.25f, 0.1f, 0.55f))
        ];
        ClimbMechanicsContact[] overhead =
        [
            new(ClimbLimb.LeftHand, new Vector3(-0.45f, 1.1f, 0f), normal, 0.9f, ClimbHoldType.Jug,
                new Vector3(-0.25f, 0.35f, 0.55f)),
            new(ClimbLimb.RightHand, new Vector3(0.45f, 1.1f, 0f), normal, 0.9f, ClimbHoldType.Jug,
                new Vector3(0.25f, 0.35f, 0.55f))
        ];

        ClimbEquilibriumResult sidewaysResult = solver.Solve(body, sideways);
        ClimbEquilibriumResult overheadResult = solver.Solve(body, overhead);

        Assert.True(sidewaysResult.ForceResidualFraction > overheadResult.ForceResidualFraction + 0.15f,
            $"Sideways residual {sidewaysResult.ForceResidualFraction:F3}, overhead {overheadResult.ForceResidualFraction:F3}.");
    }

    private static ClimbMechanicsSolver CreateSolver(Vector3 gravity) => new(new ClimbMechanicsConfiguration
    {
        Gravity = gravity,
        BodyMassKilograms = 64f,
        CharacteristicLengthMeters = 1.70f
    });

    private static ClimberBodyMassState Body(Vector3 centerOfMass) => new(centerOfMass, 64f, 1.70f);

    private static float AngleDegrees(Vector3 from, Vector3 to) =>
        MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(from), Vector3.Normalize(to)), -1f, 1f))
        * (180f / MathF.PI);

    private static SmplxPoseLandmarks NeutralSmplxPose() => new(
        Pelvis: Vector3.Zero,
        Neck: Vector3.UnitY,
        LeftShoulder: new Vector3(-0.3f, 0.8f, 0f),
        LeftElbow: new Vector3(-0.3f, 0.4f, 0f),
        LeftWrist: new Vector3(-0.3f, 0f, 0f),
        RightShoulder: new Vector3(0.3f, 0.8f, 0f),
        RightElbow: new Vector3(0.3f, 0.4f, 0f),
        RightWrist: new Vector3(0.3f, 0f, 0f),
        LeftHip: new Vector3(-0.2f, 0f, 0f),
        LeftKnee: new Vector3(-0.2f, -0.5f, 0f),
        LeftAnkle: new Vector3(-0.2f, -1f, 0f),
        LeftFoot: new Vector3(-0.2f, -1f, 0.25f),
        RightHip: new Vector3(0.2f, 0f, 0f),
        RightKnee: new Vector3(0.2f, -0.5f, 0f),
        RightAnkle: new Vector3(0.2f, -1f, 0f),
        RightFoot: new Vector3(0.2f, -1f, 0.25f));

    private static ClimbMechanicsContact[] Contacts(float wallAngleRadians)
    {
        float slope = MathF.Tan(wallAngleRadians);
        Vector3 normal = Vector3.Normalize(new Vector3(0f, -slope, 1f));
        return
        [
            Contact(ClimbLimb.LeftHand, new Vector3(-0.6f, slope, 1f), normal),
            Contact(ClimbLimb.RightHand, new Vector3(0.6f, slope, 1f), normal),
            Contact(ClimbLimb.LeftFoot, new Vector3(-0.5f, -slope, -1f), normal),
            Contact(ClimbLimb.RightFoot, new Vector3(0.5f, -slope, -1f), normal)
        ];
    }

    private static ClimbMechanicsContact Contact(ClimbLimb limb, Vector3 position, Vector3 normal) => new(
        limb,
        position,
        normal,
        quality: 0.9f,
        limb.IsFoot() ? ClimbHoldType.FootEdge : ClimbHoldType.Jug);

    private static string Describe(ClimbEquilibriumResult result) => string.Join(
        "; ",
        result.ContactForces.Select(pair => $"{pair.Key}:{pair.Value.ForceNewtons}"));
}