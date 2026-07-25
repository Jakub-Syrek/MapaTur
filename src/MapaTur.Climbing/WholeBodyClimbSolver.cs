using System.Numerics;

namespace MapaTur.Climbing;

/// <summary>
/// Renderer-independent root variables optimized by a whole-body backend. Joint angles remain owned by the
/// kinematics adapter so the same solver can drive the purchased GLB today and an SMPL-X/Pinocchio model later.
/// </summary>
public readonly record struct ClimbWholeBodyRootPose(
    Vector3 Pelvis,
    float YawRadians,
    float PitchRadians,
    float RollRadians);

/// <summary>A measured palm/sole contact and the mechanics contact associated with it.</summary>
public sealed record ClimbWholeBodyContactState(
    ClimbLimb Limb,
    Vector3 TargetPosition,
    Vector3 ActualPosition,
    ClimbMechanicsContact MechanicsContact);

/// <summary>
/// Full articulated sample returned by a rig adapter. The optimizer never assumes a particular skeleton format,
/// world-up axis, renderer, or physics engine.
/// </summary>
public sealed record ClimbWholeBodyPoseSample(
    ClimbWholeBodyRootPose RootPose,
    Vector3 CenterOfMass,
    SmplxPoseLandmarks PoseLandmarks,
    IReadOnlyList<ClimbWholeBodyContactState> Contacts,
    float MinimumBodyClearanceMeters);

public interface IClimbWholeBodyKinematics
{
    ClimbWholeBodyPoseSample Evaluate(ClimbWholeBodyRootPose rootPose);
}

public interface IWholeBodyClimbSolver
{
    ClimbWholeBodySolveResult Solve(
        ClimbWholeBodySolveRequest request,
        IClimbWholeBodyKinematics kinematics);
}

public sealed record ClimbWholeBodySolveRequest
{
    public required ClimbWholeBodyRootPose ReferencePose { get; init; }

    public required ClimbWholeBodyRootPose SeedPose { get; init; }

    public required ClimbSurfaceFrame SurfaceFrame { get; init; }

    public float BodyMassKilograms { get; init; } = 75f;

    public float CharacteristicLengthMeters { get; init; } = 1.72f;

    public float DesiredArmExtensionRatio { get; init; } = 0.94f;

    /// <summary>
    /// Optional planted foot that is actively driving the body during a movement phase. This remains renderer and
    /// axis independent: the kinematics adapter supplies the corresponding hip/ankle landmarks and SurfaceFrame
    /// supplies the route's gravity-relative up direction.
    /// </summary>
    public ClimbLimb? DrivingFoot { get; init; }

    public float DesiredDrivingLegExtensionRatio { get; init; } = 0.90f;

    public float MinimumDrivingHipAboveFootMeters { get; init; } = 0.24f;

    /// <summary>Zero for a passive hang; ramps to one while the planted leg is lifting the torso.</summary>
    public float LegDriveTaskWeight { get; init; }

    public float MaximumContactErrorMeters { get; init; } = 0.06f;

    public float MinimumBodyClearanceMeters { get; init; } = 0.015f;

    public float MaximumForceResidualFraction { get; init; } = 0.10f;

    public float MaximumMomentResidualFraction { get; init; } = 0.12f;

    public bool IncludeCoarseSeeds { get; init; } = true;

    public int RefinementPasses { get; init; } = 3;

    /// <summary>
    /// Scales the local root-search lattice. Offline/static solves use one; real-time animation can warm-start from
    /// the previous solution and use a finer, cheaper lattice without changing the backend contract.
    /// </summary>
    public float RootSearchStepScale { get; init; } = 1f;

    public int MaximumImprovementRounds { get; init; } = 5;

    public bool OptimizeOrientation { get; init; } = true;
}

public sealed record ClimbWholeBodySolverConfiguration
{
    public float MaximumGravityDropMeters { get; init; } = 0.85f;

    // A drive can lift the pelvis roughly one bent-leg length above a passive hang. Static poses do not use this
    // range unless their costs justify it, but clamping at 0.35 m made a visible stand-up mechanically impossible.
    public float MaximumGravityRiseMeters { get; init; } = 0.65f;

    public float MaximumOutwardOffsetMeters { get; init; } = 0.65f;

    public float MaximumInwardOffsetMeters { get; init; } = 0.06f;

    public float MaximumSideOffsetMeters { get; init; } = 0.30f;

    public float MaximumYawRadians { get; init; } = 30f * (MathF.PI / 180f);

    public float MaximumPitchRadians { get; init; } = 24f * (MathF.PI / 180f);

    public float MaximumRollRadians { get; init; } = 20f * (MathF.PI / 180f);

    public float ContactWeight { get; init; } = 90f;

    public float EquilibriumWeight { get; init; } = 35f;

    public float CollisionWeight { get; init; } = 140f;

    public float PosePriorWeight { get; init; } = 12f;

    public float EffortWeight { get; init; } = 4f;

    public float CoupledAnatomyWeight { get; init; } = 16f;

    public float LegDriveWeight { get; init; } = 110f;

    public float RootRegularizationWeight { get; init; } = 0.35f;
}

public sealed record ClimbWholeBodyCost(
    float Total,
    float Contact,
    float Equilibrium,
    float Collision,
    float PosePrior,
    float Effort,
    float CoupledAnatomy,
    float LegDrive,
    float RootRegularization);

public sealed record ClimbWholeBodySolveResult(
    bool IsFeasible,
    ClimbWholeBodyPoseSample Pose,
    ClimbEquilibriumResult Equilibrium,
    SmplxPoseAssessment PoseAssessment,
    ClimbWholeBodyCost Cost,
    float MaximumContactErrorMeters,
    float MeanContactErrorMeters,
    float MeanArmExtensionRatio,
    float MinimumActiveArmExtensionRatio,
    float MinimumLegExtensionRatio,
    float BothFeetAbovePelvisMeters,
    float MinimumKneeAboveHipMeters,
    float? DrivingLegExtensionRatio,
    float? DrivingHipAboveFootMeters,
    int EvaluationCount);

/// <summary>
/// Deterministic sequential whole-body optimizer inspired by SEIKO/BRLI. It alternates articulated kinematics and
/// contact-force equilibrium while searching the small root-pose space. A future native Pinocchio/SEIKO backend can
/// implement <see cref="IWholeBodyClimbSolver"/> without changing MapaTur call sites.
/// </summary>
public sealed class SequentialWholeBodyClimbSolver : IWholeBodyClimbSolver
{
    private readonly ClimbMechanicsSolver mechanics;
    private readonly SmplxPosePriorProfile posePrior;
    private readonly ClimbWholeBodySolverConfiguration configuration;

    public SequentialWholeBodyClimbSolver(
        SmplxPosePriorProfile posePrior,
        ClimbMechanicsConfiguration? mechanicsConfiguration = null,
        ClimbWholeBodySolverConfiguration? configuration = null)
    {
        this.posePrior = posePrior ?? throw new ArgumentNullException(nameof(posePrior));
        mechanics = new ClimbMechanicsSolver(mechanicsConfiguration);
        this.configuration = configuration ?? new ClimbWholeBodySolverConfiguration();
    }

    public ClimbWholeBodySolveResult Solve(
        ClimbWholeBodySolveRequest request,
        IClimbWholeBodyKinematics kinematics)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(kinematics);

        int evaluationCount = 0;
        EvaluatedPose Evaluate(ClimbWholeBodyRootPose rootPose)
        {
            evaluationCount++;
            ClimbWholeBodyPoseSample sample = kinematics.Evaluate(Clamp(rootPose, request));
            return Score(sample, request);
        }

        EvaluatedPose best = Evaluate(request.SeedPose);
        if (request.IncludeCoarseSeeds)
        {
            foreach (ClimbWholeBodyRootPose seed in BuildCoarseSeeds(request))
            {
                best = Better(best, Evaluate(seed));
            }
        }

        float stepScale = Math.Clamp(request.RootSearchStepScale, 0.15f, 2f);
        float upStep = 0.16f * stepScale;
        float sideStep = 0.10f * stepScale;
        float normalStep = 0.08f * stepScale;
        float yawStep = 8f * (MathF.PI / 180f) * stepScale;
        float pitchStep = 6f * (MathF.PI / 180f) * stepScale;
        float rollStep = 6f * (MathF.PI / 180f) * stepScale;
        int passes = Math.Clamp(request.RefinementPasses, 0, 6);
        int maximumImprovementRounds = Math.Clamp(request.MaximumImprovementRounds, 1, 5);

        for (int pass = 0; pass < passes; pass++)
        {
            bool improved;
            int improvementRounds = 0;
            do
            {
                improved = false;
                foreach (ClimbWholeBodyRootPose candidate in Neighbours(
                             best.Sample.RootPose,
                             request.SurfaceFrame,
                             upStep,
                             sideStep,
                             normalStep,
                             yawStep,
                             pitchStep,
                             rollStep,
                             request.OptimizeOrientation))
                {
                    EvaluatedPose evaluated = Evaluate(candidate);
                    EvaluatedPose chosen = Better(best, evaluated);
                    if (!ReferenceEquals(chosen, best))
                    {
                        best = chosen;
                        improved = true;
                    }
                }
            }
            while (improved && ++improvementRounds < maximumImprovementRounds);

            upStep *= 0.5f;
            sideStep *= 0.5f;
            normalStep *= 0.5f;
            yawStep *= 0.5f;
            pitchStep *= 0.5f;
            rollStep *= 0.5f;
        }

        return new ClimbWholeBodySolveResult(
            best.IsFeasible,
            best.Sample,
            best.Equilibrium,
            best.Assessment,
            best.Cost,
            best.MaximumContactError,
            best.MeanContactError,
            best.MeanArmExtension,
            best.MinimumActiveArmExtension,
            best.MinimumLegExtension,
            best.BothFeetAbovePelvis,
            best.MinimumKneeAboveHip,
            best.DrivingLegExtension,
            best.DrivingHipAboveFoot,
            evaluationCount);
    }

    private EvaluatedPose Score(ClimbWholeBodyPoseSample sample, ClimbWholeBodySolveRequest request)
    {
        if (sample.Contacts.Count == 0)
        {
            throw new InvalidOperationException("A whole-body sample must contain at least one active contact.");
        }

        ClimbEquilibriumResult equilibrium = mechanics.Solve(
            new ClimberBodyMassState(
                sample.CenterOfMass,
                request.BodyMassKilograms,
                request.CharacteristicLengthMeters),
            sample.Contacts.Select(contact => contact.MechanicsContact));
        SmplxPoseAssessment assessment = posePrior.Assess(sample.PoseLandmarks);

        float maximumContactError = 0f;
        float contactSquared = 0f;
        foreach (ClimbWholeBodyContactState contact in sample.Contacts)
        {
            float error = Vector3.Distance(contact.TargetPosition, contact.ActualPosition);
            maximumContactError = MathF.Max(maximumContactError, error);
            contactSquared += error * error;
        }

        float meanContactError = MathF.Sqrt(contactSquared / sample.Contacts.Count);
        float contactScale = MathF.Max(0.005f, request.MaximumContactErrorMeters);
        float normalizedMeanContact = meanContactError / contactScale;
        float contactCost = normalizedMeanContact * normalizedMeanContact;
        if (maximumContactError > contactScale)
        {
            float excess = (maximumContactError - contactScale) / contactScale;
            contactCost += 4f * excess * excess;
        }

        float equilibriumCost =
            (equilibrium.ForceResidualFraction * equilibrium.ForceResidualFraction)
            + (equilibrium.MomentResidualFraction * equilibrium.MomentResidualFraction);
        bool equilibriumWithinTolerance = equilibrium.ForceResidualFraction <= request.MaximumForceResidualFraction
            && equilibrium.MomentResidualFraction <= request.MaximumMomentResidualFraction;
        if (!equilibriumWithinTolerance)
        {
            equilibriumCost += 0.5f;
        }

        float clearanceScale = 0.05f;
        float missingClearance = MathF.Max(
            0f,
            request.MinimumBodyClearanceMeters - sample.MinimumBodyClearanceMeters);
        float collisionCost = Square(missingClearance / clearanceScale);

        float posePriorCost = 1f - Math.Clamp(assessment.TypicalityScore, 0f, 1f);
        posePriorCost += assessment.HardViolations.Count * 25f;

        float effortCost = equilibrium.ContactForces.Values
            .Sum(force => force.Utilization * force.Utilization);

        PoseCouplingMetrics coupling = MeasureCoupling(sample, request);
        // Passive climbing hangs lengthen each supporting arm independently. Averaging first allowed one locked arm
        // to hide one deeply bent arm; mean squared per-arm error preserves asymmetric but still loadable poses.
        float armExtensionCost = 120f * coupling.MeanSquaredActiveArmExtensionError;
        // High feet are normal in a tucked overhang. Penalize only an extreme fold, never the ordinary
        // feet-above-pelvis position required to hang low beneath the hands.
        float highFeet = MathF.Max(0f, coupling.BothFeetAbovePelvisMeters - 0.55f);
        float doubleHighStepCost = 0.35f * Square(highFeet / 0.30f);
        float compressedLegCost = Square(MathF.Max(0f, 0.48f - coupling.MinimumLegExtensionRatio) / 0.20f);
        // A knee below its hip is wrong for a high-foot tuck, but necessary when a climber stands on a foothold
        // below the pelvis. The old absolute knee>=hip guardrail made leg-powered upward movement impossible.
        float lowKneeCost = Square(coupling.KneeHeightViolationMeters / 0.20f);
        float coupledAnatomyCost = armExtensionCost + doubleHighStepCost + compressedLegCost + (4f * lowKneeCost);

        float legDriveTaskWeight = Math.Clamp(request.LegDriveTaskWeight, 0f, 1f);
        float legDriveCost = 0f;
        if (legDriveTaskWeight > 0f
            && coupling.DrivingLegExtensionRatio is { } drivingLegExtension
            && coupling.DrivingHipAboveFootMeters is { } drivingHipAboveFoot)
        {
            // A real upward move is not a root translation with decorative knees. First the pulling arms bring the
            // hip over the planted foot; then extension of that leg raises the pelvis. Penalizing both terms prevents
            // the solver from satisfying "straight leg" by extending the hip downward beneath a high foothold.
            float extensionError = (drivingLegExtension - request.DesiredDrivingLegExtensionRatio) / 0.16f;
            float missingHipHeight = MathF.Max(
                0f,
                request.MinimumDrivingHipAboveFootMeters - drivingHipAboveFoot);
            legDriveCost = legDriveTaskWeight
                * ((0.55f * Square(extensionError)) + (1.45f * Square(missingHipHeight / 0.22f)));
        }

        Vector3 delta = sample.RootPose.Pelvis - request.ReferencePose.Pelvis;
        float up = Vector3.Dot(delta, request.SurfaceFrame.UpAlongSurface);
        float side = Vector3.Dot(delta, request.SurfaceFrame.SideAlongSurface);
        float normal = Vector3.Dot(delta, request.SurfaceFrame.Normal);
        float rootCost =
            Square(up / 0.55f)
            + Square(side / 0.30f)
            + Square(normal / 0.45f)
            + Square(AngleDelta(sample.RootPose.YawRadians, request.ReferencePose.YawRadians) / configuration.MaximumYawRadians)
            + Square(AngleDelta(sample.RootPose.PitchRadians, request.ReferencePose.PitchRadians) / configuration.MaximumPitchRadians)
            + Square(AngleDelta(sample.RootPose.RollRadians, request.ReferencePose.RollRadians) / configuration.MaximumRollRadians);

        ClimbWholeBodyCost cost = new(
            (contactCost * configuration.ContactWeight)
            + (equilibriumCost * configuration.EquilibriumWeight)
            + (collisionCost * configuration.CollisionWeight)
            + (posePriorCost * configuration.PosePriorWeight)
            + (effortCost * configuration.EffortWeight)
            + (coupledAnatomyCost * configuration.CoupledAnatomyWeight)
            + (legDriveCost * configuration.LegDriveWeight)
            + (rootCost * configuration.RootRegularizationWeight),
            contactCost,
            equilibriumCost,
            collisionCost,
            posePriorCost,
            effortCost,
            coupledAnatomyCost,
            legDriveCost,
            rootCost);

        bool feasible = maximumContactError <= request.MaximumContactErrorMeters
            && sample.MinimumBodyClearanceMeters >= request.MinimumBodyClearanceMeters
            && equilibriumWithinTolerance
            && coupling.KneeHeightViolationMeters <= 0.001f
            && assessment.HardViolations.Count == 0;
        return new EvaluatedPose(
            sample,
            equilibrium,
            assessment,
            cost,
            feasible,
            maximumContactError,
            meanContactError,
            coupling.MeanArmExtensionRatio,
            coupling.MinimumActiveArmExtensionRatio,
            coupling.MinimumLegExtensionRatio,
            coupling.BothFeetAbovePelvisMeters,
            coupling.MinimumKneeAboveHipMeters,
            coupling.DrivingLegExtensionRatio,
            coupling.DrivingHipAboveFootMeters);
    }

    private PoseCouplingMetrics MeasureCoupling(
        ClimbWholeBodyPoseSample sample,
        ClimbWholeBodySolveRequest request)
    {
        SmplxPoseLandmarks pose = sample.PoseLandmarks;
        float leftArm = ExtensionRatio(pose.LeftShoulder, pose.LeftElbow, pose.LeftWrist);
        float rightArm = ExtensionRatio(pose.RightShoulder, pose.RightElbow, pose.RightWrist);
        var activeArms = new List<float>(2);
        if (sample.Contacts.Any(contact => contact.Limb == ClimbLimb.LeftHand))
        {
            activeArms.Add(leftArm);
        }

        if (sample.Contacts.Any(contact => contact.Limb == ClimbLimb.RightHand))
        {
            activeArms.Add(rightArm);
        }

        if (activeArms.Count == 0)
        {
            activeArms.Add(leftArm);
            activeArms.Add(rightArm);
        }

        float meanArm = activeArms.Average();
        float minimumArm = activeArms.Min();
        float meanSquaredArmError = activeArms.Average(extension =>
            Square(extension - request.DesiredArmExtensionRatio));
        float leftLeg = ExtensionRatio(pose.LeftHip, pose.LeftKnee, pose.LeftAnkle);
        float rightLeg = ExtensionRatio(pose.RightHip, pose.RightKnee, pose.RightAnkle);
        Vector3 gravityUp = -Vector3.Normalize(mechanics.Gravity);
        float leftFootAbovePelvis = Vector3.Dot(pose.LeftAnkle - sample.RootPose.Pelvis, gravityUp);
        float rightFootAbovePelvis = Vector3.Dot(pose.RightAnkle - sample.RootPose.Pelvis, gravityUp);
        float leftFootAboveHip = Vector3.Dot(pose.LeftAnkle - pose.LeftHip, gravityUp);
        float rightFootAboveHip = Vector3.Dot(pose.RightAnkle - pose.RightHip, gravityUp);
        float leftKneeAboveHip = Vector3.Dot(pose.LeftKnee - pose.LeftHip, gravityUp);
        float rightKneeAboveHip = Vector3.Dot(pose.RightKnee - pose.RightHip, gravityUp);
        float leftKneeHeightViolation = leftFootAboveHip >= 0f
            ? MathF.Max(0f, 0.01f - leftKneeAboveHip)
            : 0f;
        float rightKneeHeightViolation = rightFootAboveHip >= 0f
            ? MathF.Max(0f, 0.01f - rightKneeAboveHip)
            : 0f;
        float? drivingLegExtension = request.DrivingFoot switch
        {
            ClimbLimb.LeftFoot => leftLeg,
            ClimbLimb.RightFoot => rightLeg,
            _ => null
        };
        float? drivingHipAboveFoot = request.DrivingFoot switch
        {
            ClimbLimb.LeftFoot => Vector3.Dot(pose.LeftHip - pose.LeftAnkle, gravityUp),
            ClimbLimb.RightFoot => Vector3.Dot(pose.RightHip - pose.RightAnkle, gravityUp),
            _ => null
        };
        return new PoseCouplingMetrics(
            meanArm,
            minimumArm,
            meanSquaredArmError,
            MathF.Min(leftLeg, rightLeg),
            MathF.Min(leftFootAbovePelvis, rightFootAbovePelvis),
            MathF.Min(leftKneeAboveHip, rightKneeAboveHip),
            MathF.Max(leftKneeHeightViolation, rightKneeHeightViolation),
            drivingLegExtension,
            drivingHipAboveFoot);
    }

    private IEnumerable<ClimbWholeBodyRootPose> BuildCoarseSeeds(ClimbWholeBodySolveRequest request)
    {
        ClimbWholeBodyRootPose seed = request.SeedPose;
        Vector3 up = request.SurfaceFrame.UpAlongSurface;
        Vector3 normal = request.SurfaceFrame.Normal;
        yield return seed with { Pelvis = seed.Pelvis - (up * 0.18f) };
        yield return seed with { Pelvis = seed.Pelvis - (up * 0.34f) };
        yield return seed with { Pelvis = seed.Pelvis + (up * 0.14f) };
        yield return seed with { Pelvis = seed.Pelvis + (normal * 0.12f) };
        yield return seed with { Pelvis = seed.Pelvis + (normal * 0.22f) - (up * 0.18f) };
    }

    private static IEnumerable<ClimbWholeBodyRootPose> Neighbours(
        ClimbWholeBodyRootPose pose,
        ClimbSurfaceFrame frame,
        float upStep,
        float sideStep,
        float normalStep,
        float yawStep,
        float pitchStep,
        float rollStep,
        bool optimizeOrientation)
    {
        yield return pose with { Pelvis = pose.Pelvis + (frame.UpAlongSurface * upStep) };
        yield return pose with { Pelvis = pose.Pelvis - (frame.UpAlongSurface * upStep) };
        yield return pose with { Pelvis = pose.Pelvis + (frame.SideAlongSurface * sideStep) };
        yield return pose with { Pelvis = pose.Pelvis - (frame.SideAlongSurface * sideStep) };
        yield return pose with { Pelvis = pose.Pelvis + (frame.Normal * normalStep) };
        yield return pose with { Pelvis = pose.Pelvis - (frame.Normal * normalStep) };
        if (!optimizeOrientation)
        {
            yield break;
        }

        yield return pose with { YawRadians = pose.YawRadians + yawStep };
        yield return pose with { YawRadians = pose.YawRadians - yawStep };
        yield return pose with { PitchRadians = pose.PitchRadians + pitchStep };
        yield return pose with { PitchRadians = pose.PitchRadians - pitchStep };
        yield return pose with { RollRadians = pose.RollRadians + rollStep };
        yield return pose with { RollRadians = pose.RollRadians - rollStep };
    }

    private ClimbWholeBodyRootPose Clamp(
        ClimbWholeBodyRootPose candidate,
        ClimbWholeBodySolveRequest request)
    {
        Vector3 delta = candidate.Pelvis - request.ReferencePose.Pelvis;
        float up = Math.Clamp(
            Vector3.Dot(delta, request.SurfaceFrame.UpAlongSurface),
            -configuration.MaximumGravityDropMeters,
            configuration.MaximumGravityRiseMeters);
        float side = Math.Clamp(
            Vector3.Dot(delta, request.SurfaceFrame.SideAlongSurface),
            -configuration.MaximumSideOffsetMeters,
            configuration.MaximumSideOffsetMeters);
        float normal = Math.Clamp(
            Vector3.Dot(delta, request.SurfaceFrame.Normal),
            -configuration.MaximumInwardOffsetMeters,
            configuration.MaximumOutwardOffsetMeters);
        return candidate with
        {
            Pelvis = request.ReferencePose.Pelvis
                + (request.SurfaceFrame.UpAlongSurface * up)
                + (request.SurfaceFrame.SideAlongSurface * side)
                + (request.SurfaceFrame.Normal * normal),
            YawRadians = ClampAround(
                candidate.YawRadians,
                request.ReferencePose.YawRadians,
                configuration.MaximumYawRadians),
            PitchRadians = ClampAround(
                candidate.PitchRadians,
                request.ReferencePose.PitchRadians,
                configuration.MaximumPitchRadians),
            RollRadians = ClampAround(
                candidate.RollRadians,
                request.ReferencePose.RollRadians,
                configuration.MaximumRollRadians)
        };
    }

    private static EvaluatedPose Better(EvaluatedPose current, EvaluatedPose candidate)
    {
        if (candidate.IsFeasible != current.IsFeasible)
        {
            return candidate.IsFeasible ? candidate : current;
        }

        return candidate.Cost.Total + 1e-5f < current.Cost.Total ? candidate : current;
    }

    private static float ExtensionRatio(Vector3 root, Vector3 middle, Vector3 end)
    {
        float reach = Vector3.Distance(root, middle) + Vector3.Distance(middle, end);
        return reach > 1e-5f ? Math.Clamp(Vector3.Distance(root, end) / reach, 0f, 1f) : 0f;
    }

    private static float ClampAround(float value, float center, float radius) =>
        center + Math.Clamp(AngleDelta(value, center), -radius, radius);

    private static float AngleDelta(float value, float center)
    {
        float delta = value - center;
        while (delta > MathF.PI)
        {
            delta -= 2f * MathF.PI;
        }

        while (delta < -MathF.PI)
        {
            delta += 2f * MathF.PI;
        }

        return delta;
    }

    private static float Square(float value) => value * value;

    private sealed record EvaluatedPose(
        ClimbWholeBodyPoseSample Sample,
        ClimbEquilibriumResult Equilibrium,
        SmplxPoseAssessment Assessment,
        ClimbWholeBodyCost Cost,
        bool IsFeasible,
        float MaximumContactError,
        float MeanContactError,
        float MeanArmExtension,
        float MinimumActiveArmExtension,
        float MinimumLegExtension,
        float BothFeetAbovePelvis,
        float MinimumKneeAboveHip,
        float? DrivingLegExtension,
        float? DrivingHipAboveFoot);

    private readonly record struct PoseCouplingMetrics(
        float MeanArmExtensionRatio,
        float MinimumActiveArmExtensionRatio,
        float MeanSquaredActiveArmExtensionError,
        float MinimumLegExtensionRatio,
        float BothFeetAbovePelvisMeters,
        float MinimumKneeAboveHipMeters,
        float KneeHeightViolationMeters,
        float? DrivingLegExtensionRatio,
        float? DrivingHipAboveFootMeters);
}