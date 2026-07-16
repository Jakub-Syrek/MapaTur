using System.Numerics;

using MapaTur.Climbing;

namespace MapaTur.Climbing.Tests;

public sealed class WholeBodyClimbSolverTests
{
    [Fact]
    public void Coupled_solver_should_allow_low_hang_with_feet_above_pelvis()
    {
        Vector3 gravity = new(0f, -9.81f, 0f);
        ClimbSurfaceFrame surface = ClimbSurfaceFrame.Create(Vector3.Zero, Vector3.UnitZ, gravity);
        var rig = new SyntheticClimberRig(gravity, surface, _ => 0.08f);
        SequentialWholeBodyClimbSolver solver = CreateCouplingSolver(gravity);
        ClimbWholeBodyRootPose root = new(Vector3.Zero, 0f, 0f, 0f);

        ClimbWholeBodySolveResult result = solver.Solve(Request(root, surface), rig);

        Assert.True(result.Pose.RootPose.Pelvis.Y < -0.05f,
            $"Expected a low long-arm hang, got {result.Pose.RootPose.Pelvis}.");
        Assert.True(result.BothFeetAbovePelvisMeters > 0.25f,
            $"The solver incorrectly rejected a normal high-foot tuck: {result.BothFeetAbovePelvisMeters:F3} m.");
        Assert.True(result.MinimumKneeAboveHipMeters >= -0.01f,
            $"A knee crossed below its hip: {result.MinimumKneeAboveHipMeters:F3} m.");
    }

    [Fact]
    public void Collision_constraint_should_move_body_outward_without_moving_contacts()
    {
        Vector3 gravity = new(0f, -9.81f, 0f);
        ClimbSurfaceFrame surface = ClimbSurfaceFrame.Create(Vector3.Zero, Vector3.UnitZ, gravity);
        var rig = new SyntheticClimberRig(
            gravity,
            surface,
            root => Vector3.Dot(root.Pelvis, surface.Normal) - 0.02f);
        SequentialWholeBodyClimbSolver solver = CreateCollisionSolver(gravity);
        ClimbWholeBodyRootPose root = new(Vector3.Zero, 0f, 0f, 0f);

        ClimbWholeBodySolveResult result = solver.Solve(Request(root, surface), rig);

        Assert.True(result.Pose.RootPose.Pelvis.Z >= 0.034f,
            $"Body did not move outside the wall: {result.Pose.RootPose.Pelvis}.");
        Assert.InRange(result.MaximumContactErrorMeters, 0f, 0.0001f);
        Assert.True(result.Pose.MinimumBodyClearanceMeters >= 0.015f);
    }

    [Fact]
    public void Coupled_solver_should_use_mapatur_z_up_gravity_axis()
    {
        Vector3 gravity = new(0f, 0f, -9.81f);
        ClimbSurfaceFrame surface = ClimbSurfaceFrame.Create(Vector3.Zero, Vector3.UnitY, gravity);
        var rig = new SyntheticClimberRig(gravity, surface, _ => 0.08f);
        SequentialWholeBodyClimbSolver solver = CreateCouplingSolver(gravity);
        ClimbWholeBodyRootPose root = new(Vector3.Zero, 0f, 0f, 0f);

        ClimbWholeBodySolveResult result = solver.Solve(Request(root, surface), rig);

        Assert.True(result.Pose.RootPose.Pelvis.Z < -0.05f,
            $"Expected a gravity-aligned Z-up hang correction, got {result.Pose.RootPose.Pelvis}.");
        Assert.True(result.MinimumKneeAboveHipMeters >= -0.01f);
    }

    [Fact]
    public void Leg_drive_task_should_raise_pelvis_and_extend_planted_leg()
    {
        Vector3 gravity = new(0f, -9.81f, 0f);
        ClimbSurfaceFrame surface = ClimbSurfaceFrame.Create(Vector3.Zero, Vector3.UnitZ, gravity);
        var rig = new SyntheticClimberRig(gravity, surface, _ => 0.08f, footHeight: -0.35f);
        SequentialWholeBodyClimbSolver solver = CreateCouplingSolver(gravity);
        ClimbWholeBodyRootPose root = new(Vector3.Zero, 0f, 0f, 0f);

        ClimbWholeBodySolveResult passive = solver.Solve(Request(root, surface), rig);
        ClimbWholeBodySolveResult driving = solver.Solve(
            Request(root, surface) with
            {
                DesiredArmExtensionRatio = 0.70f,
                DrivingFoot = ClimbLimb.RightFoot,
                DesiredDrivingLegExtensionRatio = 0.90f,
                MinimumDrivingHipAboveFootMeters = 0.30f,
                LegDriveTaskWeight = 1f
            },
            rig);

        Assert.True(driving.Pose.RootPose.Pelvis.Y >= passive.Pose.RootPose.Pelvis.Y + 0.20f,
            $"The drive did not lift the pelvis: passive={passive.Pose.RootPose.Pelvis.Y:F3}, driving={driving.Pose.RootPose.Pelvis.Y:F3}, " +
            $"leg={driving.DrivingLegExtensionRatio:F3}, hipAboveFoot={driving.DrivingHipAboveFootMeters:F3}, " +
            $"arm={driving.MeanArmExtensionRatio:F3}, feasible={driving.IsFeasible}, cost={driving.Cost}.");
        Assert.True(driving.DrivingLegExtensionRatio > passive.MinimumLegExtensionRatio,
            $"The planted leg did not extend: passive={passive.MinimumLegExtensionRatio:F3}, driving={driving.DrivingLegExtensionRatio:F3}.");
        Assert.True(driving.DrivingHipAboveFootMeters >= 0.20f,
            $"The hip never moved above the driving foot: {driving.DrivingHipAboveFootMeters:F3} m.");
    }

    private static SequentialWholeBodyClimbSolver CreateCouplingSolver(Vector3 gravity) => new(
        SmplxPosePriorProfile.CreateBootstrap(),
        Mechanics(gravity),
        new ClimbWholeBodySolverConfiguration
        {
            ContactWeight = 100f,
            EquilibriumWeight = 0f,
            CollisionWeight = 0f,
            PosePriorWeight = 0f,
            EffortWeight = 0f,
            CoupledAnatomyWeight = 100f,
            RootRegularizationWeight = 0.05f,
            // This regression isolates gravity-axis handling. Outward sag has its own collision test below.
            MaximumOutwardOffsetMeters = 0f,
            MaximumInwardOffsetMeters = 0f
        });

    private static SequentialWholeBodyClimbSolver CreateCollisionSolver(Vector3 gravity) => new(
        SmplxPosePriorProfile.CreateBootstrap(),
        Mechanics(gravity),
        new ClimbWholeBodySolverConfiguration
        {
            ContactWeight = 100f,
            EquilibriumWeight = 0f,
            CollisionWeight = 500f,
            PosePriorWeight = 0f,
            EffortWeight = 0f,
            CoupledAnatomyWeight = 0f,
            RootRegularizationWeight = 0.05f
        });

    private static ClimbMechanicsConfiguration Mechanics(Vector3 gravity) => new()
    {
        Gravity = gravity,
        SolverIterations = 40,
        ForceResidualTolerance = 10f,
        MomentResidualTolerance = 10f
    };

    private static ClimbWholeBodySolveRequest Request(
        ClimbWholeBodyRootPose root,
        ClimbSurfaceFrame surface) => new()
        {
            ReferencePose = root,
            SeedPose = root,
            SurfaceFrame = surface,
            DesiredArmExtensionRatio = 0.94f,
            MaximumContactErrorMeters = 0.01f,
            MinimumBodyClearanceMeters = 0.015f,
            IncludeCoarseSeeds = true,
            RefinementPasses = 3
        };

    private sealed class SyntheticClimberRig(
        Vector3 gravity,
        ClimbSurfaceFrame surface,
        Func<ClimbWholeBodyRootPose, float> clearance,
        float footHeight = 0.25f)
        : IClimbWholeBodyKinematics
    {
        private readonly Vector3 gravityUp = -Vector3.Normalize(gravity);
        private readonly Vector3 leftHand = (surface.SideAlongSurface * -0.52f) + (surface.UpAlongSurface * 1.52f);
        private readonly Vector3 rightHand = (surface.SideAlongSurface * 0.52f) + (surface.UpAlongSurface * 1.52f);
        private readonly Vector3 leftFoot = (surface.SideAlongSurface * -0.42f) + (surface.UpAlongSurface * footHeight);
        private readonly Vector3 rightFoot = (surface.SideAlongSurface * 0.42f) + (surface.UpAlongSurface * footHeight);

        public ClimbWholeBodyPoseSample Evaluate(ClimbWholeBodyRootPose rootPose)
        {
            Vector3 pelvis = rootPose.Pelvis;
            Vector3 leftShoulder = pelvis + (surface.SideAlongSurface * -0.22f) + (gravityUp * 0.48f);
            Vector3 rightShoulder = pelvis + (surface.SideAlongSurface * 0.22f) + (gravityUp * 0.48f);
            Vector3 leftElbow = MiddleOnTwoBoneCircle(leftShoulder, leftHand, 0.62f, -surface.SideAlongSurface);
            Vector3 rightElbow = MiddleOnTwoBoneCircle(rightShoulder, rightHand, 0.62f, surface.SideAlongSurface);
            Vector3 leftHip = pelvis + (surface.SideAlongSurface * -0.17f);
            Vector3 rightHip = pelvis + (surface.SideAlongSurface * 0.17f);
            Vector3 leftKnee = MiddleOnTwoBoneCircle(leftHip, leftFoot, 0.58f, gravityUp - surface.SideAlongSurface);
            Vector3 rightKnee = MiddleOnTwoBoneCircle(rightHip, rightFoot, 0.58f, gravityUp + surface.SideAlongSurface);
            Vector3 leftToe = BuildToe(leftKnee, leftFoot);
            Vector3 rightToe = BuildToe(rightKnee, rightFoot);
            var landmarks = new SmplxPoseLandmarks(
                pelvis,
                pelvis + (gravityUp * 0.82f),
                leftShoulder,
                leftElbow,
                leftHand,
                rightShoulder,
                rightElbow,
                rightHand,
                leftHip,
                leftKnee,
                leftFoot,
                leftToe,
                rightHip,
                rightKnee,
                rightFoot,
                rightToe);

            ClimbWholeBodyContactState[] contacts =
            [
                Contact(ClimbLimb.LeftHand, leftHand, leftShoulder),
                Contact(ClimbLimb.RightHand, rightHand, rightShoulder),
                Contact(ClimbLimb.LeftFoot, leftFoot, leftHip),
                Contact(ClimbLimb.RightFoot, rightFoot, rightHip)
            ];
            return new ClimbWholeBodyPoseSample(
                rootPose,
                pelvis + (gravityUp * 0.18f),
                landmarks,
                contacts,
                clearance(rootPose));
        }

        private ClimbWholeBodyContactState Contact(ClimbLimb limb, Vector3 point, Vector3 proximal) => new(
            limb,
            point,
            point,
            new ClimbMechanicsContact(limb, point, surface.Normal, 1f, ClimbHoldType.Jug, proximal));

        private Vector3 BuildToe(Vector3 knee, Vector3 ankle)
        {
            Vector3 shin = knee - ankle;
            Vector3 toeDirection = surface.Normal - (Vector3.Normalize(shin) * Vector3.Dot(surface.Normal, Vector3.Normalize(shin)));
            if (toeDirection.LengthSquared() < 1e-6f)
            {
                toeDirection = Vector3.Cross(shin, surface.SideAlongSurface);
            }

            return ankle + (Vector3.Normalize(toeDirection) * 0.22f);
        }

        private static Vector3 MiddleOnTwoBoneCircle(
            Vector3 proximal,
            Vector3 distal,
            float segmentLength,
            Vector3 preferredBend)
        {
            Vector3 offset = distal - proximal;
            float distance = Math.Clamp(offset.Length(), 0.30f, (2f * segmentLength) - 0.001f);
            Vector3 direction = offset.LengthSquared() > 1e-8f ? Vector3.Normalize(offset) : Vector3.UnitY;
            Vector3 bend = preferredBend - (direction * Vector3.Dot(preferredBend, direction));
            if (bend.LengthSquared() < 1e-8f)
            {
                bend = Vector3.Cross(direction, Vector3.UnitZ);
            }

            float halfDistance = distance * 0.5f;
            float bendHeight = MathF.Sqrt(MathF.Max(0f, (segmentLength * segmentLength) - (halfDistance * halfDistance)));
            return proximal + (direction * halfDistance) + (Vector3.Normalize(bend) * bendHeight);
        }
    }
}