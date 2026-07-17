using System.Numerics;

using MapaTur.Climbing;

namespace MapaTur.Application.Terrain;

/// <summary>
/// <see cref="IClimbWholeBodyKinematics"/> over MapaTur's <see cref="SkinnedModel"/> — the seam through
/// which the whole-body climb solver drives an actual rig. Ported from the Climber3d PoC adapter
/// (analytic two-bone IK with pole vectors + short CCD cleanup, palm correction pass, ankle cone,
/// capsule wall clearance, anthropometric COM).
///
/// Spaces: the model poses in its own glTF space (+Y up, model units); climb space is X-east/Y-north/Z-up
/// REAL metres. The base axis map (yaw 0) faces the model toward -Y; the root pose then yaws about +Z.
/// Vertical exaggeration never enters here — callers convert with <see cref="ClimbSpaceTransform"/>.
/// Not thread-safe (it drives one scratch <see cref="SkinnedModel"/>); give each worker its own instance.
/// </summary>
public sealed class ClimberRigKinematics : IClimbWholeBodyKinematics
{
    private const float MinimumFlexionDegrees = 8f;      // limb never locks dead straight
    private const float MaximumFlexionDegrees = 150f;    // nor folds beyond anatomy
    private const int CcdIterations = 6;
    private const float CcdToleranceMeters = 0.03f;
    private const float PalmCorrectionGain = 0.80f;
    private const float PalmCorrectionClampMeters = 0.16f;

    private readonly SkinnedModel model;
    private readonly float scale;                        // model units -> climb metres
    private readonly Vector3 hipsBind;                   // model space, bind pose
    private readonly string pelvisBone;
    private readonly string chestBone;
    private readonly string headBone;
    private readonly LimbChain leftArm;
    private readonly LimbChain rightArm;
    private readonly LimbChain leftLeg;
    private readonly LimbChain rightLeg;
    private readonly Vector3 leftArmPole;
    private readonly Vector3 rightArmPole;
    private readonly Vector3 leftLegPole;
    private readonly Vector3 rightLegPole;
    private readonly float characterHeightMeters;

    private IClimbSurface? surface;
    private IReadOnlyDictionary<ClimbLimb, LimbContact> contacts =
        new Dictionary<ClimbLimb, LimbContact>();
    private Vector3 gravity = ClimbWorld.Gravity;

    public ClimberRigKinematics(SkinnedModel model, ClimberRigProfile profile, float characterHeightMeters = 1.7f)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(profile);
        this.model = model;
        this.characterHeightMeters = characterHeightMeters;

        pelvisBone = Resolve(profile.Pelvis);
        chestBone = Resolve(profile.Chest);
        headBone = Resolve(profile.Head);
        leftArm = new LimbChain(
            Resolve(profile.LeftUpperArm), Resolve(profile.LeftLowerArm), Resolve(profile.LeftWrist), Resolve(profile.LeftPalm));
        rightArm = new LimbChain(
            Resolve(profile.RightUpperArm), Resolve(profile.RightLowerArm), Resolve(profile.RightWrist), Resolve(profile.RightPalm));
        leftLeg = new LimbChain(
            Resolve(profile.LeftUpperLeg), Resolve(profile.LeftLowerLeg), Resolve(profile.LeftAnkle), Resolve(profile.LeftToes));
        rightLeg = new LimbChain(
            Resolve(profile.RightUpperLeg), Resolve(profile.RightLowerLeg), Resolve(profile.RightAnkle), Resolve(profile.RightToes));

        model.ResetPose();
        hipsBind = BonePosition(pelvisBone);

        // Scale from the SKELETON span, never from mesh bounds: bundled props (weapons, hood) pollute the
        // bind AABB, and a stylized rig (short legs, long torso) must still land at the requested height.
        Vector3 headBind = BonePosition(headBone);
        Vector3 chestBind = BonePosition(chestBone);
        float headTop = headBind.Y + (Vector3.Distance(headBind, chestBind) * 0.6f);
        float feetBottom = MathF.Min(
            MathF.Min(BonePosition(leftLeg.Tip).Y, BonePosition(rightLeg.Tip).Y),
            MathF.Min(BonePosition(leftLeg.Effector).Y, BonePosition(rightLeg.Effector).Y));
        scale = characterHeightMeters / MathF.Max(headTop - feetBottom, 1e-3f);

        // Measured reach and joint anchors — the planner and tests must use THESE, not human anthropometry:
        // a stylized rig can have arms/legs far shorter than its nominal height suggests.
        ArmReachMeters = ChainLength(leftArm, includeTip: true) * scale;   // contact = palm
        LegReachMeters = ChainLength(leftLeg, includeTip: false) * scale;  // contact = ankle (lifted target)
        PelvisHeightMeters = hipsBind.Y * scale;
        LeftShoulderOffsetMeters = ToClimbOffset(BonePosition(leftArm.Root) - hipsBind);
        LeftHipOffsetMeters = ToClimbOffset(BonePosition(leftLeg.Root) - hipsBind);

        // Side/forward signs are measured on the actual bind pose instead of assuming a glTF convention:
        // "outward" for the left arm is wherever the left wrist actually sits, and "forward" is where the
        // toes point relative to the ankles. This keeps one profile working across differently exported rigs.
        float leftOutward = MathF.Sign(BonePosition(leftArm.Effector).X - BonePosition(rightArm.Effector).X);
        leftOutward = leftOutward == 0f ? 1f : leftOutward;
        float forward = MathF.Sign(
            (BonePosition(leftLeg.Tip).Z + BonePosition(rightLeg.Tip).Z)
            - (BonePosition(leftLeg.Effector).Z + BonePosition(rightLeg.Effector).Z));
        forward = forward == 0f ? 1f : forward;

        // Climber3d pole directions: elbows strongly outward, below the shoulder-hand line, slightly forward;
        // knees dominated by up/toward-chest with a small forward and lateral component.
        leftArmPole = new Vector3(4.0f * leftOutward, -0.70f, 0.25f * forward);
        rightArmPole = new Vector3(-4.0f * leftOutward, -0.70f, 0.25f * forward);
        leftLegPole = new Vector3(0.14f * leftOutward, 1.20f, 0.34f * forward);
        rightLegPole = new Vector3(-0.14f * leftOutward, 1.20f, 0.34f * forward);
    }

    /// <summary>Shoulder→palm chain length in metres, measured on the actual rig.</summary>
    public float ArmReachMeters { get; }

    /// <summary>Hip→ankle chain length in metres, measured on the actual rig.</summary>
    public float LegReachMeters { get; }

    /// <summary>Standing pelvis height above the feet in metres, measured on the actual rig.</summary>
    public float PelvisHeightMeters { get; }

    /// <summary>Left shoulder relative to the pelvis in climb frame at yaw 0 (left = +X); mirror X for the right.</summary>
    public Vector3 LeftShoulderOffsetMeters { get; }

    /// <summary>Left hip joint relative to the pelvis in climb frame at yaw 0 (left = +X); mirror X for the right.</summary>
    public Vector3 LeftHipOffsetMeters { get; }

    /// <summary>The wall, the four holds, and gravity for subsequent <see cref="Evaluate"/> calls.</summary>
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

        model.ResetPose();

        // 1) Limb IK to the per-limb hold contact points (converted into model space through the root pose).
        foreach ((ClimbLimb limb, LimbContact contact) in contacts)
        {
            Vector3 targetClimb = contact.Hold.ContactPointFor(limb, gravity);
            Vector3 targetModel = ClimbToModel(targetClimb, rootPose);
            LimbChain chain = ChainFor(limb);
            Vector3 pole = PoleFor(limb);
            SolveLimb(chain, targetModel, pole, keepMiddleAboveRoot: limb.IsFoot());

            if (limb.IsHand())
            {
                // The wrist joint is not the palm: measure the palm miss and re-aim the wrist by a clamped
                // fraction of it (Climber3d's single bounded correction — repeating it caused wrist chatter).
                Vector3 palm = BonePosition(chain.Tip);
                Vector3 error = targetModel - palm;
                float clamp = PalmCorrectionClampMeters / scale;
                Vector3 shift = error * PalmCorrectionGain;
                if (shift.Length() > clamp)
                {
                    shift = Vector3.Normalize(shift) * clamp;
                }

                SolveLimb(chain, targetModel + shift, pole, keepMiddleAboveRoot: false);
            }
            else
            {
                OrientFoot(chain, contact, rootPose);
            }
        }

        // 2) Landmarks in climb space.
        SmplxPoseLandmarks landmarks = BuildLandmarks(rootPose);

        // 3) Measured contacts + mechanics contacts (forces act at the measured palm/ankle, with the real
        //    shoulder/hip as the proximal axis for the tension/friction cones).
        List<ClimbWholeBodyContactState> contactStates = [];
        foreach ((ClimbLimb limb, LimbContact contact) in contacts)
        {
            LimbChain chain = ChainFor(limb);
            Vector3 targetClimb = contact.Hold.ContactPointFor(limb, gravity);
            Vector3 actualClimb = ModelToClimb(
                limb.IsHand() ? BonePosition(chain.Tip) : BonePosition(chain.Effector), rootPose);
            Vector3 proximal = ModelToClimb(BonePosition(chain.Root), rootPose);
            var mechanics = new ClimbMechanicsContact(
                limb,
                actualClimb,
                contact.Hold.Normal,
                contact.Hold.Quality * (1f - (contact.Fatigue * 0.55f)),
                contact.Hold.Type,
                proximal);
            contactStates.Add(new ClimbWholeBodyContactState(limb, targetClimb, actualClimb, mechanics));
        }

        // 4) Anthropometric COM (Dempster-style segment fractions) and capsule wall clearance.
        Vector3 centerOfMass = ComputeCenterOfMass(landmarks);
        float clearance = ComputeMinimumClearance(landmarks);

        return new ClimbWholeBodyPoseSample(rootPose, centerOfMass, landmarks, contactStates, clearance);
    }

    /// <summary>World matrix that places the CURRENT posed model at the given root pose for rendering
    /// (climb space; multiply the translation Z by vertical exaggeration outside).</summary>
    public Matrix4x4 BuildWorldMatrix(ClimbWholeBodyRootPose rootPose)
    {
        Matrix4x4 axisMap = new(
            1f, 0f, 0f, 0f,
            0f, 0f, 1f, 0f,
            0f, -1f, 0f, 0f,
            0f, 0f, 0f, 1f); // (x, y, z)model -> (x, -z, y)climb
        return Matrix4x4.CreateTranslation(-hipsBind)
            * Matrix4x4.CreateScale(scale)
            * axisMap
            * Matrix4x4.CreateRotationX(rootPose.PitchRadians)
            * Matrix4x4.CreateRotationY(rootPose.RollRadians)
            * Matrix4x4.CreateRotationZ(rootPose.YawRadians)
            * Matrix4x4.CreateTranslation(rootPose.Pelvis);
    }

    private void SolveLimb(LimbChain chain, Vector3 targetModel, Vector3 pole, bool keepMiddleAboveRoot)
    {
        Vector3 root = BonePosition(chain.Root);
        Vector3 middle = BonePosition(chain.Middle);
        Vector3 end = BonePosition(chain.Effector);
        float upperLength = Vector3.Distance(root, middle);
        float lowerLength = Vector3.Distance(middle, end);
        if (upperLength < 1e-4f || lowerLength < 1e-4f)
        {
            return;
        }

        // The elbow/knee flexion window becomes a reachable-distance window (law of cosines).
        float minimumDistance = EndDistance(upperLength, lowerLength, MaximumFlexionDegrees);
        float maximumDistance = EndDistance(upperLength, lowerLength, MinimumFlexionDegrees);
        Vector3 toTarget = targetModel - root;
        float targetDistance = toTarget.Length();
        if (targetDistance < 1e-5f)
        {
            return;
        }

        Vector3 direction = toTarget / targetDistance;
        float solvedDistance = Math.Clamp(targetDistance, minimumDistance, maximumDistance);
        Vector3 clampedTarget = root + (direction * solvedDistance);

        Vector3 bendDirection = pole - (Vector3.Dot(direction, pole) * direction);
        if (bendDirection.LengthSquared() < 1e-4f)
        {
            Vector3 reference = MathF.Abs(direction.X) < 0.8f ? Vector3.UnitX : Vector3.UnitY;
            bendDirection = Vector3.Cross(direction, reference);
        }

        bendDirection = Vector3.Normalize(bendDirection);
        if (keepMiddleAboveRoot && targetModel.Y >= root.Y)
        {
            // Gravity-relative knee rule: when stepping at or above the hip, prefer the bend plane that
            // raises the knee instead of folding it below the hip.
            Vector3 upwardBend = Vector3.UnitY - (Vector3.Dot(direction, Vector3.UnitY) * direction);
            if (upwardBend.LengthSquared() > 1e-4f)
            {
                upwardBend = Vector3.Normalize(upwardBend);
                if (upwardBend.Y > bendDirection.Y)
                {
                    bendDirection = upwardBend;
                }
            }
        }

        float alongTarget = ((upperLength * upperLength) - (lowerLength * lowerLength)
            + (solvedDistance * solvedDistance)) / (2f * solvedDistance);
        float bendHeight = MathF.Sqrt(MathF.Max(0f, (upperLength * upperLength) - (alongTarget * alongTarget)));
        Vector3 desiredMiddle = root + (direction * alongTarget) + (bendDirection * bendHeight);

        RotateBoneToward(chain.Root, middle, desiredMiddle, root);

        float tolerance = CcdToleranceMeters / scale;
        for (int iteration = 0; iteration < CcdIterations; iteration++)
        {
            Vector3 currentMiddle = BonePosition(chain.Middle);
            Vector3 currentEnd = BonePosition(chain.Effector);
            if (Vector3.Distance(currentEnd, clampedTarget) <= tolerance)
            {
                break;
            }

            RotateBoneToward(chain.Middle, currentEnd, clampedTarget, currentMiddle);
        }
    }

    private void OrientFoot(LimbChain leg, LimbContact contact, ClimbWholeBodyRootPose rootPose)
    {
        Vector3 knee = BonePosition(leg.Middle);
        Vector3 ankle = BonePosition(leg.Effector);
        Vector3 toes = BonePosition(leg.Tip);
        if (Vector3.DistanceSquared(ankle, toes) < 1e-8f)
        {
            return; // rig without a toe bone - nothing to orient
        }

        // Point the forefoot into the wall and slightly down (Climber3d toe aim), then clamp to the
        // anatomical shin-toe cone.
        ClimbSurfaceFrame frame = ClimbSurfaceFrame.Create(contact.Hold.Position, contact.Hold.Normal, gravity);
        Vector3 toeAimClimb = Vector3.Normalize((-frame.Normal * 0.72f) - (frame.UpAlongSurface * 0.69f));
        Vector3 ankleClimb = ModelToClimb(ankle, rootPose);
        float toeLength = Vector3.Distance(ankle, toes);
        Vector3 desiredToeModel = ClimbToModel(ankleClimb + (toeAimClimb * toeLength * scale), rootPose);

        Vector3 constrained = ClimbAnkleOrientation.ConstrainToeDirection(knee, ankle, toes, desiredToeModel);
        RotateBoneToward(leg.Effector, toes, ankle + (constrained * toeLength), ankle);
    }

    private void RotateBoneToward(string bone, Vector3 currentChild, Vector3 desiredChild, Vector3 pivot)
    {
        Quaternion rotation = FromToRotation(currentChild - pivot, desiredChild - pivot);
        if (rotation != Quaternion.Identity)
        {
            model.RotateBoneModelSpace(bone, rotation);
        }
    }

    private SmplxPoseLandmarks BuildLandmarks(ClimbWholeBodyRootPose rootPose)
    {
        Vector3 chest = BonePosition(chestBone);
        Vector3 head = BonePosition(headBone);
        Vector3 Map(Vector3 modelPoint) => ModelToClimb(modelPoint, rootPose);
        return new SmplxPoseLandmarks(
            Map(BonePosition(pelvisBone)),
            Map(Vector3.Lerp(chest, head, 0.65f)),
            Map(BonePosition(leftArm.Root)),
            Map(BonePosition(leftArm.Middle)),
            Map(BonePosition(leftArm.Effector)),
            Map(BonePosition(rightArm.Root)),
            Map(BonePosition(rightArm.Middle)),
            Map(BonePosition(rightArm.Effector)),
            Map(BonePosition(leftLeg.Root)),
            Map(BonePosition(leftLeg.Middle)),
            Map(BonePosition(leftLeg.Effector)),
            Map(BonePosition(leftLeg.Tip)),
            Map(BonePosition(rightLeg.Root)),
            Map(BonePosition(rightLeg.Middle)),
            Map(BonePosition(rightLeg.Effector)),
            Map(BonePosition(rightLeg.Tip)));
    }

    private static Vector3 ComputeCenterOfMass(SmplxPoseLandmarks lm)
    {
        Vector3 com = ((lm.Pelvis + lm.Neck) * 0.5f * 0.497f)
            + (lm.Neck + ((lm.Neck - lm.Pelvis) * 0.18f)) * 0.081f;
        com += ((lm.LeftShoulder + lm.LeftElbow) * 0.5f * 0.028f) + ((lm.LeftElbow + lm.LeftWrist) * 0.5f * 0.022f);
        com += ((lm.RightShoulder + lm.RightElbow) * 0.5f * 0.028f) + ((lm.RightElbow + lm.RightWrist) * 0.5f * 0.022f);
        com += ((lm.LeftHip + lm.LeftKnee) * 0.5f * 0.100f) + ((lm.LeftKnee + lm.LeftAnkle) * 0.5f * 0.061f);
        com += ((lm.RightHip + lm.RightKnee) * 0.5f * 0.100f) + ((lm.RightKnee + lm.RightAnkle) * 0.5f * 0.061f);
        return com;
    }

    private float ComputeMinimumClearance(SmplxPoseLandmarks lm)
    {
        if (surface is null)
        {
            return float.MaxValue;
        }

        // Capsule fractions of body height ported from the PoC colliders (pelvis..head).
        Span<(Vector3 Centre, float RadiusFraction)> capsules =
        [
            (lm.Pelvis, 0.0806f),
            (Vector3.Lerp(lm.Pelvis, lm.Neck, 0.45f), 0.0871f),
            (Vector3.Lerp(lm.Pelvis, lm.Neck, 0.80f), 0.0935f),
            (lm.Neck, 0.0903f),
            (Vector3.Lerp(lm.Neck, lm.Neck + (lm.Neck - lm.Pelvis) * 0.25f, 0.5f), 0.0516f),
            (lm.Neck + ((lm.Neck - lm.Pelvis) * 0.20f), 0.0613f)
        ];

        float minimum = float.MaxValue;
        foreach ((Vector3 centre, float radiusFraction) in capsules)
        {
            Vector3 projected = surface.ProjectToSurface(centre);
            ClimbSurfaceFrame frame = surface.SampleSurface(centre, gravity);
            float clearance = Vector3.Dot(centre - projected, frame.Normal)
                - (radiusFraction * characterHeightMeters);
            minimum = MathF.Min(minimum, clearance);
        }

        return minimum;
    }

    // ---- model <-> climb space -------------------------------------------------------------------

    private Vector3 ModelToClimb(Vector3 modelPoint, ClimbWholeBodyRootPose pose)
    {
        Vector3 relative = (modelPoint - hipsBind) * scale;
        Vector3 climb = new(relative.X, -relative.Z, relative.Y);
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
        Vector3 modelRelative = new(relative.X, relative.Z, -relative.Y);
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

    // ---- helpers ----------------------------------------------------------------------------------

    private static float EndDistance(float upperLength, float lowerLength, float flexionDegrees)
    {
        float flexion = flexionDegrees * (MathF.PI / 180f);
        // flexion 0 = straight limb (distance upper+lower); growing flexion folds the joint.
        return MathF.Sqrt(MathF.Max(
            1e-8f,
            (upperLength * upperLength) + (lowerLength * lowerLength)
            + (2f * upperLength * lowerLength * MathF.Cos(flexion))));
    }

    private static Quaternion FromToRotation(Vector3 from, Vector3 to)
    {
        float fromLength = from.Length();
        float toLength = to.Length();
        if (fromLength < 1e-6f || toLength < 1e-6f)
        {
            return Quaternion.Identity;
        }

        Vector3 f = from / fromLength;
        Vector3 t = to / toLength;
        float dot = Math.Clamp(Vector3.Dot(f, t), -1f, 1f);
        if (dot > 1f - 1e-7f)
        {
            return Quaternion.Identity;
        }

        if (dot < -1f + 1e-7f)
        {
            Vector3 orthogonal = MathF.Abs(f.X) < 0.8f ? Vector3.UnitX : Vector3.UnitY;
            Vector3 flipAxis = Vector3.Normalize(Vector3.Cross(f, orthogonal));
            return Quaternion.CreateFromAxisAngle(flipAxis, MathF.PI);
        }

        Vector3 axis = Vector3.Normalize(Vector3.Cross(f, t));
        return Quaternion.CreateFromAxisAngle(axis, MathF.Acos(dot));
    }

    private Vector3 BonePosition(string bone) =>
        model.GetBonePosedPositionStrict(bone)
        ?? throw new InvalidOperationException($"Bone '{bone}' disappeared from the rig.");

    private float ChainLength(LimbChain chain, bool includeTip) =>
        Vector3.Distance(BonePosition(chain.Root), BonePosition(chain.Middle))
        + Vector3.Distance(BonePosition(chain.Middle), BonePosition(chain.Effector))
        + (includeTip ? Vector3.Distance(BonePosition(chain.Effector), BonePosition(chain.Tip)) : 0f);

    /// <summary>Bind-pose model offset → climb-frame offset at yaw 0 (metres): (x, y, z) → (x, −z, y)·scale.</summary>
    private Vector3 ToClimbOffset(Vector3 modelOffset) =>
        new Vector3(modelOffset.X, -modelOffset.Z, modelOffset.Y) * scale;

    private LimbChain ChainFor(ClimbLimb limb) => limb switch
    {
        ClimbLimb.LeftHand => leftArm,
        ClimbLimb.RightHand => rightArm,
        ClimbLimb.LeftFoot => leftLeg,
        _ => rightLeg
    };

    private Vector3 PoleFor(ClimbLimb limb) => limb switch
    {
        ClimbLimb.LeftHand => leftArmPole,
        ClimbLimb.RightHand => rightArmPole,
        ClimbLimb.LeftFoot => leftLegPole,
        _ => rightLegPole
    };

    private string Resolve(string[] aliases)
    {
        foreach (string alias in aliases)
        {
            if (model.BoneNames.Contains(alias))
            {
                return alias;
            }
        }

        throw new ArgumentException(
            $"None of the bone aliases [{string.Join(", ", aliases)}] exist in this rig "
            + $"(available: {string.Join(", ", model.BoneNames)}).");
    }

    private readonly record struct LimbChain(string Root, string Middle, string Effector, string Tip);
}