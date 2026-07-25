using System.Numerics;

using MapaTur.Climbing;

namespace MapaTur.Application.Terrain;

/// <summary>
/// <see cref="IClimbWholeBodyKinematics"/> over the taken-over <see cref="ClimberSkinnedModel"/> — the
/// thin Z-up wrapper the integration handoff prescribed: MapaTur supplies climb-space holds and a root
/// pose in X-east/Y-north/Z-up REAL metres, the proven Climber3d rig does all posing in its own model
/// space. The base axis map is derived from the rig's measured forward axis, so the same wrapper survives
/// differently exported models. Not thread-safe; one instance per driving thread.
/// </summary>
public sealed class RealisticClimberRig : IClimbWholeBodyKinematics
{
    private readonly ClimberSkinnedModel model;
    private readonly SmplxPosePriorProfile posePrior;
    private readonly float scale;             // model units -> climb metres
    private readonly Vector3 hipsBind;        // model space
    private readonly float forwardSign;       // model forward Z sign; +forward maps onto climb -Y at yaw 0

    private IClimbSurface? surface;
    private IReadOnlyDictionary<ClimbLimb, LimbContact> contacts = new Dictionary<ClimbLimb, LimbContact>();
    private Vector3 gravity = ClimbWorld.Gravity;

    public RealisticClimberRig(ClimberSkinnedModel model, float characterHeightMeters = 1.85f)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!model.SupportsContactIk)
        {
            throw new ArgumentException("The model does not expose the bone set required for contact IK.", nameof(model));
        }

        this.model = model;
        posePrior = SmplxPosePriorProfile.CreateBootstrap();
        CharacterHeightMeters = characterHeightMeters;
        scale = characterHeightMeters / MathF.Max(model.BindBoundsMax.Y - model.BindBoundsMin.Y, 1e-3f);
        hipsBind = model.HipsBindPosition;

        ClimberSkinnedModel.FootOrientationPositions feet = model.GetFootOrientationPositions();
        float forward = MathF.Sign((feet.LeftToe.Z + feet.RightToe.Z) - (feet.LeftAnkle.Z + feet.RightAnkle.Z));
        forwardSign = forward == 0f ? 1f : forward;

        ClimberSkinnedModel.ArmJointPositions arms = model.GetArmJointPositions();
        ClimberSkinnedModel.LegJointPositions legs = model.GetLegJointPositions();
        ArmReachMeters = (Vector3.Distance(arms.LeftShoulder, arms.LeftElbow)
            + Vector3.Distance(arms.LeftElbow, arms.LeftHand)) * scale;
        LegReachMeters = (Vector3.Distance(legs.LeftHip, legs.LeftKnee)
            + Vector3.Distance(legs.LeftKnee, legs.LeftFoot)) * scale;
        LeftShoulderOffsetMeters = MapDirection(arms.LeftShoulder - hipsBind) * scale;
        LeftHipOffsetMeters = MapDirection(legs.LeftHip - hipsBind) * scale;
        PelvisHeightMeters = (hipsBind.Y - model.BindBoundsMin.Y) * scale;
    }

    public float CharacterHeightMeters { get; }

    public float ArmReachMeters { get; }

    public float LegReachMeters { get; }

    public Vector3 LeftShoulderOffsetMeters { get; }

    public Vector3 LeftHipOffsetMeters { get; }

    public float PelvisHeightMeters { get; }

    public ClimberSkinnedModel Model => model;

    public void SetClimbContext(
        IClimbSurface climbSurface,
        IReadOnlyDictionary<ClimbLimb, LimbContact> limbContacts,
        Vector3 gravityVector)
    {
        surface = climbSurface ?? throw new ArgumentNullException(nameof(climbSurface));
        contacts = limbContacts ?? throw new ArgumentNullException(nameof(limbContacts));
        gravity = gravityVector;
    }

    public ClimbWholeBodyPoseSample Evaluate(ClimbWholeBodyRootPose rootPose)
    {
        if (surface is null)
        {
            throw new InvalidOperationException("Call SetClimbContext before Evaluate.");
        }

        // Contact + toe-aim targets in climb space, then into the rig's model space through the root pose.
        Vector3 TargetFor(ClimbLimb limb) => contacts.TryGetValue(limb, out LimbContact? contact)
            ? contact.Hold.ContactPointFor(limb, gravity)
            : rootPose.Pelvis;
        Vector3 ToeAim(ClimbLimb limb, Vector3 ankleTargetClimb)
        {
            if (!contacts.TryGetValue(limb, out LimbContact? contact))
            {
                return ankleTargetClimb;
            }

            ClimbSurfaceFrame frame = ClimbSurfaceFrame.Create(contact.Hold.Position, contact.Hold.Normal, gravity);
            Vector3 toeDirection = Vector3.Normalize((-frame.Normal * 0.72f) - (frame.UpAlongSurface * 0.69f));
            return ankleTargetClimb + (toeDirection * 0.25f);
        }

        Vector3 leftFootClimb = TargetFor(ClimbLimb.LeftFoot);
        Vector3 rightFootClimb = TargetFor(ClimbLimb.RightFoot);
        var targets = new ClimberSkinnedModel.ContactTargets(
            ClimbToModel(TargetFor(ClimbLimb.LeftHand), rootPose),
            ClimbToModel(TargetFor(ClimbLimb.RightHand), rootPose),
            ClimbToModel(leftFootClimb, rootPose),
            ClimbToModel(rightFootClimb, rootPose),
            ClimbToModel(ToeAim(ClimbLimb.LeftFoot, leftFootClimb), rootPose),
            ClimbToModel(ToeAim(ClimbLimb.RightFoot, rightFootClimb), rootPose));

        model.PoseContacts(targets, posePrior, updateSkinnedMesh: false);

        // Landmarks + measured contacts back in climb space.
        ClimberSkinnedModel.ArmJointPositions arms = model.GetArmJointPositions();
        ClimberSkinnedModel.LegJointPositions legs = model.GetLegJointPositions();
        ClimberSkinnedModel.FootOrientationPositions feet = model.GetFootOrientationPositions();
        ClimberSkinnedModel.HandGripPositions grips = model.GetHandGripPositions();
        Vector3 hips = model.GetBonePosition("mixamorig:Hips_01") ?? hipsBind;
        Vector3 chest = model.GetBonePosition("mixamorig:Spine2_04") ?? hips;
        Vector3 head = model.GetBonePosition("mixamorig:Head_06") ?? chest;
        Vector3 Map(Vector3 modelPoint) => ModelToClimb(modelPoint, rootPose);

        var landmarks = new SmplxPoseLandmarks(
            Map(hips),
            Map(Vector3.Lerp(chest, head, 0.65f)),
            Map(arms.LeftShoulder),
            Map(arms.LeftElbow),
            Map(grips.LeftWrist),
            Map(arms.RightShoulder),
            Map(arms.RightElbow),
            Map(grips.RightWrist),
            Map(legs.LeftHip),
            Map(legs.LeftKnee),
            Map(legs.LeftFoot),
            Map(feet.LeftToe),
            Map(legs.RightHip),
            Map(legs.RightKnee),
            Map(legs.RightFoot),
            Map(feet.RightToe));

        List<ClimbWholeBodyContactState> contactStates = [];
        foreach ((ClimbLimb limb, LimbContact contact) in contacts)
        {
            Vector3 targetClimb = contact.Hold.ContactPointFor(limb, gravity);
            Vector3 actualClimb = limb switch
            {
                ClimbLimb.LeftHand => Map(grips.LeftIndex),
                ClimbLimb.RightHand => Map(grips.RightIndex),
                ClimbLimb.LeftFoot => Map(legs.LeftFoot),
                _ => Map(legs.RightFoot)
            };
            Vector3 proximal = limb switch
            {
                ClimbLimb.LeftHand => Map(arms.LeftShoulder),
                ClimbLimb.RightHand => Map(arms.RightShoulder),
                ClimbLimb.LeftFoot => Map(legs.LeftHip),
                _ => Map(legs.RightHip)
            };
            var mechanics = new ClimbMechanicsContact(
                limb,
                actualClimb,
                contact.Hold.Normal,
                contact.Hold.Quality * (1f - (contact.Fatigue * 0.55f)),
                contact.Hold.Type,
                proximal);
            contactStates.Add(new ClimbWholeBodyContactState(limb, targetClimb, actualClimb, mechanics));
        }

        Vector3 centerOfMass = Map(model.GetBodyCenterOfMass());
        float clearance = ComputeMinimumClearance(landmarks);
        return new ClimbWholeBodyPoseSample(rootPose, centerOfMass, landmarks, contactStates, clearance);
    }

    /// <summary>Skins the CURRENT pose into the model's posed buffers (the proven parallel pass).</summary>
    public void Skin() => model.Skin();

    /// <summary>World matrix placing the posed model at the root pose in climb space (multiply the
    /// translation Z by vertical exaggeration outside before rendering).</summary>
    public Matrix4x4 BuildWorldMatrix(ClimbWholeBodyRootPose rootPose)
    {
        float s = forwardSign;
        var axisMap = new Matrix4x4(
            s, 0f, 0f, 0f,
            0f, 0f, 1f, 0f,
            0f, -s, 0f, 0f,
            0f, 0f, 0f, 1f);
        return Matrix4x4.CreateTranslation(-hipsBind)
            * Matrix4x4.CreateScale(scale)
            * axisMap
            * Matrix4x4.CreateRotationX(rootPose.PitchRadians)
            * Matrix4x4.CreateRotationY(rootPose.RollRadians)
            * Matrix4x4.CreateRotationZ(rootPose.YawRadians)
            * Matrix4x4.CreateTranslation(rootPose.Pelvis);
    }

    private float ComputeMinimumClearance(SmplxPoseLandmarks lm)
    {
        if (surface is null)
        {
            return float.MaxValue;
        }

        // The PoC collider set (pelvis..head), radii as fractions of its 3.10 m calibration height.
        Span<(Vector3 Centre, float RadiusFraction)> capsules =
        [
            (lm.Pelvis, 0.0806f),
            (Vector3.Lerp(lm.Pelvis, lm.Neck, 0.45f), 0.0871f),
            (Vector3.Lerp(lm.Pelvis, lm.Neck, 0.80f), 0.0935f),
            (lm.Neck, 0.0903f),
            (lm.Neck + ((lm.Neck - lm.Pelvis) * 0.20f), 0.0613f)
        ];

        float minimum = float.MaxValue;
        foreach ((Vector3 centre, float radiusFraction) in capsules)
        {
            Vector3 projected = surface.ProjectToSurface(centre);
            ClimbSurfaceFrame frame = surface.SampleSurface(centre, gravity);
            float clearance = Vector3.Dot(centre - projected, frame.Normal)
                - (radiusFraction * CharacterHeightMeters);
            minimum = MathF.Min(minimum, clearance);
        }

        return minimum;
    }

    // ---- model <-> climb space (base map derived from the measured forward axis) --------------------

    private Vector3 MapDirection(Vector3 modelDirection) =>
        new(forwardSign * modelDirection.X, -forwardSign * modelDirection.Z, modelDirection.Y);

    private Vector3 ModelToClimb(Vector3 modelPoint, ClimbWholeBodyRootPose pose)
    {
        Vector3 climb = MapDirection(modelPoint - hipsBind) * scale;
        climb = RotateAboutX(climb, pose.PitchRadians);
        climb = RotateAboutY(climb, pose.RollRadians);
        climb = RotateAboutZ(climb, pose.YawRadians);
        return pose.Pelvis + climb;
    }

    private Vector3 ClimbToModel(Vector3 climbPoint, ClimbWholeBodyRootPose pose)
    {
        Vector3 relative = climbPoint - pose.Pelvis;
        relative = RotateAboutZ(relative, -pose.YawRadians);
        relative = RotateAboutY(relative, -pose.RollRadians);
        relative = RotateAboutX(relative, -pose.PitchRadians);
        var modelRelative = new Vector3(forwardSign * relative.X, relative.Z, -forwardSign * relative.Y);
        return hipsBind + (modelRelative / scale);
    }

    private static Vector3 RotateAboutX(Vector3 v, float radians)
    {
        (float sin, float cos) = MathF.SinCos(radians);
        return new Vector3(v.X, (v.Y * cos) - (v.Z * sin), (v.Y * sin) + (v.Z * cos));
    }

    private static Vector3 RotateAboutY(Vector3 v, float radians)
    {
        (float sin, float cos) = MathF.SinCos(radians);
        return new Vector3((v.X * cos) + (v.Z * sin), v.Y, (-v.X * sin) + (v.Z * cos));
    }

    private static Vector3 RotateAboutZ(Vector3 v, float radians)
    {
        (float sin, float cos) = MathF.SinCos(radians);
        return new Vector3((v.X * cos) - (v.Y * sin), (v.X * sin) + (v.Y * cos), v.Z);
    }
}