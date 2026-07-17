using System.Numerics;

using MapaTur.Climbing;

using SharpGLTF.Runtime;
using SharpGLTF.Schema2;
using SharpGLTF.Transforms;

namespace MapaTur.Application.Terrain;

/// <summary>
/// The PROVEN climber rig taken over verbatim from Climber3d's SkinnedClimberModel (baseline 7335a954):
/// contact IK calibrated for the licensed realistic climber (pole vectors, distal-finger end effectors,
/// ankle cone, flexion-window clamps, lower-bone-only CCD cleanup) plus the SharpGLTF CPU-skinning path
/// that demonstrably renders an articulated body. The only additions are skinned NORMALS (MapaTur's
/// renderer lights the model; the PoC viewer did not) and public visibility. Do not "improve" the IK
/// here — every constant was visually calibrated against this rig in the PoC.
/// </summary>
public sealed class ClimberSkinnedModel
{
    private const string HipsBone = "mixamorig:Hips_01";
    private const string LeftHandBone = "mixamorig:LeftHand_011";
    private const string RightHandBone = "mixamorig:RightHand_022";
    private const string LeftFootBone = "mixamorig:LeftFoot_033";
    private const string RightFootBone = "mixamorig:RightFoot_038";

    private static readonly string[] LeftArmChainNames = ["mixamorig:LeftArm_09", "mixamorig:LeftForeArm_010"];
    private static readonly string[] RightArmChainNames = ["mixamorig:RightArm_00", "mixamorig:RightForeArm_021"];
    private static readonly string[] LeftLegChainNames = ["mixamorig:LeftUpLeg_031", "mixamorig:LeftLeg_032"];
    private static readonly string[] RightLegChainNames = ["mixamorig:RightUpLeg_036", "mixamorig:RightLeg_037"];

    private static readonly ReadSettings LenientRead = new()
    {
        Validation = SharpGLTF.Validation.ValidationMode.TryFix,
    };

    private readonly SceneInstance instance;
    private readonly ArmatureInstance armature;
    private readonly Matrix4x4[] bindLocals;
    private readonly Dictionary<string, int> nodeByName;
    private readonly Dictionary<string, string> boneAliases = new(StringComparer.Ordinal);
    private readonly string hipsBone;
    private readonly string leftHandBone;
    private readonly string rightHandBone;
    private readonly string leftFootBone;
    private readonly string rightFootBone;
    private readonly string leftToeBone;
    private readonly string rightToeBone;
    private readonly string leftIndexFingerBone;
    private readonly string leftPinkyFingerBone;
    private readonly string rightIndexFingerBone;
    private readonly string rightPinkyFingerBone;
    private readonly string[] leftArmChain;
    private readonly string[] rightArmChain;
    private readonly string[] leftLegChain;
    private readonly string[] rightLegChain;

    private ClimberSkinnedModel(
        string sourcePath,
        IReadOnlyList<Primitive> primitives,
        SceneInstance instance,
        IReadOnlyList<byte[]> baseColorTextures)
    {
        SourcePath = sourcePath;
        Primitives = primitives;
        this.instance = instance;
        armature = instance.Armature;
        BaseColorTextures = baseColorTextures;
        bindLocals = new Matrix4x4[armature.LogicalNodes.Count];
        nodeByName = new Dictionary<string, int>(armature.LogicalNodes.Count);
        for (int index = 0; index < armature.LogicalNodes.Count; index++)
        {
            NodeInstance node = armature.LogicalNodes[index];
            bindLocals[index] = node.LocalMatrix;
            if (!string.IsNullOrWhiteSpace(node.Name))
            {
                nodeByName[node.Name] = index;
            }
        }

        hipsBone = ResolveBone(HipsBone, "Root_M", "Hips_M", "Hips") ?? string.Empty;
        leftHandBone = ResolveBone(LeftHandBone, "Wrist_L", "Hand_L", "LeftHand") ?? string.Empty;
        rightHandBone = ResolveBone(RightHandBone, "Wrist_R", "Hand_R", "RightHand") ?? string.Empty;
        leftFootBone = ResolveBone(LeftFootBone, "Ankle_L", "Foot_L", "LeftFoot") ?? string.Empty;
        rightFootBone = ResolveBone(RightFootBone, "Ankle_R", "Foot_R", "RightFoot") ?? string.Empty;
        leftToeBone = ResolveBone("Toes_L", "Toe_L", "LeftToeBase") ?? string.Empty;
        rightToeBone = ResolveBone("Toes_R", "Toe_R", "RightToeBase") ?? string.Empty;
        // Use the distal finger bones as the visual grip marker. Finger1 is the knuckle and sits close to the wrist;
        // targeting it made the forearm appear to catch the hold while the visible fingertips remained beside it.
        leftIndexFingerBone = ResolveBone("IndexFinger4_L", "Index_4_L", "Index_L") ?? string.Empty;
        leftPinkyFingerBone = ResolveBone("PinkyFinger4_L", "Pinky_4_L", "Pinky_L") ?? string.Empty;
        rightIndexFingerBone = ResolveBone("IndexFinger4_R", "Index_4_R", "Index_R") ?? string.Empty;
        rightPinkyFingerBone = ResolveBone("PinkyFinger4_R", "Pinky_4_R", "Pinky_R") ?? string.Empty;
        leftArmChain = ResolveChain(LeftArmChainNames, ["Shoulder_L", "Elbow_L"], ["LeftShoulder", "LeftElbow"]);
        rightArmChain = ResolveChain(RightArmChainNames, ["Shoulder_R", "Elbow_R"], ["RightShoulder", "RightElbow"]);
        leftLegChain = ResolveChain(LeftLegChainNames, ["Hip_L", "Knee_L"], ["LeftHip", "LeftKnee"]);
        rightLegChain = ResolveChain(RightLegChainNames, ["Hip_R", "Knee_R"], ["RightHip", "RightKnee"]);

        // The wall-collision layer uses the old Mixamo spine labels. Map them to equivalent joints when
        // a different rig is selected, while preserving the original labels for the existing asset.
        AddAlias("mixamorig:Spine_02", ResolveBone("mixamorig:Spine_02", "Spine1_M", "Spine_M") ?? hipsBone);
        AddAlias("mixamorig:Spine1_03", ResolveBone("mixamorig:Spine1_03", "Spine2_M", "Spine1_M") ?? hipsBone);
        AddAlias("mixamorig:Spine2_04", ResolveBone("mixamorig:Spine2_04", "Chest_M", "Spine2_M") ?? hipsBone);
        AddAlias("mixamorig:Neck_05", ResolveBone("mixamorig:Neck_05", "Neck_M") ?? hipsBone);
        AddAlias("mixamorig:Head_06", ResolveBone("mixamorig:Head_06", "Head_M") ?? hipsBone);
    }

    public sealed class Primitive
    {
        public required Vector3[] BindPositions { get; init; }

        public required Vector3[] BindNormals { get; init; }

        public required Vector2[] TexCoords { get; init; }

        public required SparseWeight8[] SkinWeights { get; init; }

        public required uint[] Indices { get; init; }

        public required int MeshIndex { get; init; }

        public required Vector3[] PosedPositions { get; init; }

        public required Vector3[] PosedNormals { get; init; }

        public required int BaseColorTextureIndex { get; init; }
    }

    public string SourcePath { get; }

    public IReadOnlyList<Primitive> Primitives { get; }

    public IReadOnlyList<byte[]> BaseColorTextures { get; }

    public byte[]? BaseColorTextureBytes => BaseColorTextures.FirstOrDefault();

    public int VertexCount => Primitives.Sum(primitive => primitive.PosedPositions.Length);

    public int BoneCount => armature.LogicalNodes.Count;

    public Vector3 BindBoundsMin { get; private set; }

    public Vector3 BindBoundsMax { get; private set; }

    public Vector3 HipsBindPosition { get; private set; }

    public bool SupportsContactIk => HasAllBones(hipsBone, leftHandBone, rightHandBone, leftFootBone, rightFootBone)
        && leftArmChain.Length == 2
        && rightArmChain.Length == 2
        && leftLegChain.Length == 2
        && rightLegChain.Length == 2;

    public static ClimberSkinnedModel Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ModelRoot model = ModelRoot.Load(path, LenientRead);
        var primitives = new List<Primitive>();
        var baseColorTextures = new List<byte[]>();
        var textureIndexByImage = new Dictionary<int, int>();

        foreach (Mesh mesh in model.LogicalMeshes)
        {
            foreach (MeshPrimitive primitive in mesh.Primitives)
            {
                IList<Vector3>? positions = primitive.GetVertexAccessor("POSITION")?.AsVector3Array();
                if (positions is null || positions.Count == 0)
                {
                    continue;
                }

                IList<Vector3>? normals = primitive.GetVertexAccessor("NORMAL")?.AsVector3Array();
                IList<Vector4>? joints = primitive.GetVertexAccessor("JOINTS_0")?.AsVector4Array();
                IList<Vector4>? weights = primitive.GetVertexAccessor("WEIGHTS_0")?.AsVector4Array();
                IList<Vector2>? texCoords = primitive.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
                MaterialChannel? baseColorChannel = primitive.Material?.FindChannel("BaseColor");
                int baseColorTextureIndex = -1;
                if (baseColorChannel is { } channel
                    && channel.Texture?.PrimaryImage is { } image)
                {
                    if (!textureIndexByImage.TryGetValue(image.LogicalIndex, out baseColorTextureIndex))
                    {
                        baseColorTextureIndex = baseColorTextures.Count;
                        textureIndexByImage[image.LogicalIndex] = baseColorTextureIndex;
                        baseColorTextures.Add(image.Content.Content.ToArray());
                    }
                }

                int vertexCount = positions.Count;
                var bindPositions = new Vector3[vertexCount];
                var bindNormals = new Vector3[vertexCount];
                var skinWeights = new SparseWeight8[vertexCount];
                for (int vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
                {
                    bindPositions[vertexIndex] = positions[vertexIndex];
                    bindNormals[vertexIndex] = normals is not null ? normals[vertexIndex] : Vector3.UnitY;
                    skinWeights[vertexIndex] = joints is not null && weights is not null
                        ? SparseWeight8.Create(joints[vertexIndex], weights[vertexIndex])
                        : default;
                }

                primitives.Add(new Primitive
                {
                    BindPositions = bindPositions,
                    BindNormals = bindNormals,
                    TexCoords = texCoords?.ToArray() ?? new Vector2[vertexCount],
                    SkinWeights = skinWeights,
                    Indices = primitive.GetIndices() is { Count: > 0 } indices
                        ? indices.ToArray()
                        : SequentialIndices(vertexCount),
                    MeshIndex = mesh.LogicalIndex,
                    PosedPositions = new Vector3[vertexCount],
                    PosedNormals = new Vector3[vertexCount],
                    BaseColorTextureIndex = baseColorTextureIndex,
                });
            }
        }

        Scene scene = model.DefaultScene ?? model.LogicalScenes[0];
        SceneInstance instance = SceneTemplate.Create(scene).CreateInstance();
        var result = new ClimberSkinnedModel(path, primitives, instance, baseColorTextures);
        result.ResetPose();
        result.Skin();
        (result.BindBoundsMin, result.BindBoundsMax) = result.GetPosedBounds();
        result.HipsBindPosition = result.GetBonePosition(result.hipsBone)
            ?? throw new InvalidDataException("The model does not contain a supported hips/root bone.");
        return result;
    }

    /// <summary>
    /// Builds a deterministic climbing pose from the four actual hand/foot contacts. It deliberately resets the
    /// baked clip: a generic looping clip cannot guarantee that a limb touches a gameplay hold. Each two-bone
    /// limb is solved analytically with a preferred elbow/knee pole, then the normal SharpGLTF skinning path deforms
    /// the mesh. A pole fixes the bend plane, avoiding the mechanical twisting and occasional knee/elbow inversion
    /// of an unconstrained CCD loop.
    /// </summary>
    public void PoseContacts(
        ContactTargets targets,
        SmplxPosePriorProfile posePrior,
        bool updateSkinnedMesh = true,
        ClimbLimb? drivingFoot = null,
        float legDriveWeight = 0f)
    {
        ArgumentNullException.ThrowIfNull(posePrior);
        if (!SupportsContactIk)
        {
            ResetPose();
            if (updateSkinnedMesh)
            {
                Skin();
            }

            return;
        }

        ResetPose();
        // The imported rig's local forward axis is opposite to the viewer-facing convention. Arms use a positive-Z
        // pole to keep elbows in front of the torso. Its world-facing transform mirrors model-space X, so the left
        // pole must be positive X and the right pole negative X. Negative Y keeps both elbows below the shoulder-hand
        // line instead of letting the solver fold them upward toward the chest.
        SolveTwoBoneLimb(string.IsNullOrWhiteSpace(leftIndexFingerBone) ? leftHandBone : leftIndexFingerBone,
            leftArmChain, targets.LeftHand, new Vector3(4.0f, -0.70f, 0.25f),
            posePrior.GetEnvelope(SmplxPoseFeatureNames.LeftElbowFlexion));
        SolveTwoBoneLimb(string.IsNullOrWhiteSpace(rightIndexFingerBone) ? rightHandBone : rightIndexFingerBone,
            rightArmChain, targets.RightHand, new Vector3(-4.0f, -0.70f, 0.25f),
            posePrior.GetEnvelope(SmplxPoseFeatureNames.RightElbowFlexion));
        // The knee is primarily a flexion/extension joint; the hip supplies the smaller abduction/external rotation.
        SolveTwoBoneLimb(
            leftFootBone,
            leftLegChain,
            targets.LeftFoot,
            new Vector3(0.14f, 1.20f, 0.34f),
            posePrior.GetEnvelope(SmplxPoseFeatureNames.LeftKneeFlexion),
            keepMiddleAboveRoot: drivingFoot != ClimbLimb.LeftFoot || legDriveWeight < 0.12f);
        SolveTwoBoneLimb(
            rightFootBone,
            rightLegChain,
            targets.RightFoot,
            new Vector3(-0.14f, 1.20f, 0.34f),
            posePrior.GetEnvelope(SmplxPoseFeatureNames.RightKneeFlexion),
            keepMiddleAboveRoot: drivingFoot != ClimbLimb.RightFoot || legDriveWeight < 0.12f);
        OrientFoot(
            leftFootBone,
            leftToeBone,
            leftLegChain[1],
            targets.LeftToe,
            posePrior.GetEnvelope(SmplxPoseFeatureNames.LeftAnkleAngle));
        OrientFoot(
            rightFootBone,
            rightToeBone,
            rightLegChain[1],
            targets.RightToe,
            posePrior.GetEnvelope(SmplxPoseFeatureNames.RightAnkleAngle));
        if (updateSkinnedMesh)
        {
            Skin();
        }
    }

    public Vector3? GetBonePosition(string boneName)
    {
        string resolvedName = boneAliases.TryGetValue(boneName, out string? alias) ? alias : boneName;
        return nodeByName.TryGetValue(resolvedName, out int index)
            ? armature.LogicalNodes[index].ModelMatrix.Translation
            : null;
    }

    public HandGripPositions GetHandGripPositions() => new(
        GetRequiredBonePosition(leftHandBone),
        GetOptionalBonePosition(leftIndexFingerBone, leftHandBone),
        GetOptionalBonePosition(leftPinkyFingerBone, leftHandBone),
        GetRequiredBonePosition(rightHandBone),
        GetOptionalBonePosition(rightIndexFingerBone, rightHandBone),
        GetOptionalBonePosition(rightPinkyFingerBone, rightHandBone));

    public LegJointPositions GetLegJointPositions() => new(
        GetRequiredBonePosition(leftLegChain[0]),
        GetRequiredBonePosition(leftLegChain[1]),
        GetRequiredBonePosition(leftFootBone),
        GetRequiredBonePosition(rightLegChain[0]),
        GetRequiredBonePosition(rightLegChain[1]),
        GetRequiredBonePosition(rightFootBone));

    public FootOrientationPositions GetFootOrientationPositions() => new(
        GetRequiredBonePosition(leftLegChain[1]),
        GetRequiredBonePosition(leftFootBone),
        GetOptionalBonePosition(leftToeBone, leftFootBone),
        GetRequiredBonePosition(rightLegChain[1]),
        GetRequiredBonePosition(rightFootBone),
        GetOptionalBonePosition(rightToeBone, rightFootBone));

    public ArmJointPositions GetArmJointPositions() => new(
        GetRequiredBonePosition(leftArmChain[0]),
        GetRequiredBonePosition(leftArmChain[1]),
        GetOptionalBonePosition(leftIndexFingerBone, leftHandBone),
        GetRequiredBonePosition(rightArmChain[0]),
        GetRequiredBonePosition(rightArmChain[1]),
        GetOptionalBonePosition(rightIndexFingerBone, rightHandBone));

    /// <summary>
    /// Anthropometric centre-of-mass estimate from the posed skeleton. Segment fractions are normalized to one and
    /// deliberately live in the model adapter; the reusable mechanics solver only consumes the resulting world point.
    /// </summary>
    public Vector3 GetBodyCenterOfMass()
    {
        Vector3 hips = GetRequiredBonePosition(hipsBone);
        Vector3 chest = GetBonePosition("mixamorig:Spine2_04") ?? hips;
        Vector3 head = GetBonePosition("mixamorig:Head_06") ?? chest;
        ArmJointPositions arms = GetArmJointPositions();
        LegJointPositions legs = GetLegJointPositions();

        Vector3 weighted = Midpoint(hips, chest) * 0.497f;
        weighted += head * 0.081f;
        weighted += Midpoint(arms.LeftShoulder, arms.LeftElbow) * 0.028f;
        weighted += Midpoint(arms.RightShoulder, arms.RightElbow) * 0.028f;
        weighted += Midpoint(arms.LeftElbow, arms.LeftHand) * 0.016f;
        weighted += Midpoint(arms.RightElbow, arms.RightHand) * 0.016f;
        weighted += arms.LeftHand * 0.006f;
        weighted += arms.RightHand * 0.006f;
        weighted += Midpoint(legs.LeftHip, legs.LeftKnee) * 0.100f;
        weighted += Midpoint(legs.RightHip, legs.RightKnee) * 0.100f;
        weighted += Midpoint(legs.LeftKnee, legs.LeftFoot) * 0.0465f;
        weighted += Midpoint(legs.RightKnee, legs.RightFoot) * 0.0465f;
        weighted += legs.LeftFoot * 0.0145f;
        weighted += legs.RightFoot * 0.0145f;
        return weighted;
    }

    public (Vector3 Min, Vector3 Max) GetPosedBounds()
    {
        Vector3 min = new(float.PositiveInfinity);
        Vector3 max = new(float.NegativeInfinity);
        foreach (Primitive primitive in Primitives)
        {
            foreach (Vector3 position in primitive.PosedPositions)
            {
                min = Vector3.Min(min, position);
                max = Vector3.Max(max, position);
            }
        }

        return float.IsFinite(min.X) ? (min, max) : (Vector3.Zero, Vector3.Zero);
    }

    /// <summary>The proven CPU-skinning pass (parallel across primitives), extended with normals.</summary>
    public void Skin()
    {
        var transforms = new Dictionary<int, IGeometryTransform>();
        foreach (DrawableInstance drawable in instance)
        {
            transforms[drawable.Template.LogicalMeshIndex] = drawable.Transform;
        }

        Parallel.ForEach(Primitives, primitive =>
        {
            if (!transforms.TryGetValue(primitive.MeshIndex, out IGeometryTransform? transform))
            {
                Array.Copy(primitive.BindPositions, primitive.PosedPositions, primitive.BindPositions.Length);
                Array.Copy(primitive.BindNormals, primitive.PosedNormals, primitive.BindNormals.Length);
                return;
            }

            for (int vertexIndex = 0; vertexIndex < primitive.BindPositions.Length; vertexIndex++)
            {
                primitive.PosedPositions[vertexIndex] = transform.TransformPosition(
                    primitive.BindPositions[vertexIndex],
                    null,
                    primitive.SkinWeights[vertexIndex]);
                primitive.PosedNormals[vertexIndex] = Vector3.Normalize(transform.TransformNormal(
                    primitive.BindNormals[vertexIndex],
                    null,
                    primitive.SkinWeights[vertexIndex]));
            }
        });
    }

    private static Vector3 Midpoint(Vector3 first, Vector3 second) => (first + second) * 0.5f;

    private void ResetPose()
    {
        for (int index = 0; index < bindLocals.Length; index++)
        {
            armature.LogicalNodes[index].LocalMatrix = bindLocals[index];
        }
    }

    private void OrientFoot(
        string ankleBone,
        string toeBone,
        string kneeBone,
        Vector3 desiredToe,
        SmplxJointEnvelope ankleEnvelope)
    {
        if (string.IsNullOrWhiteSpace(toeBone)
            || !nodeByName.TryGetValue(ankleBone, out int ankleIndex)
            || !nodeByName.TryGetValue(toeBone, out int toeIndex)
            || !nodeByName.TryGetValue(kneeBone, out int kneeIndex))
        {
            return;
        }

        NodeInstance ankle = armature.LogicalNodes[ankleIndex];
        NodeInstance toe = armature.LogicalNodes[toeIndex];
        Vector3 anklePosition = ankle.ModelMatrix.Translation;
        Vector3 neutralToe = toe.ModelMatrix.Translation;
        float toeLength = Vector3.Distance(anklePosition, neutralToe);
        if (toeLength < 0.0001f)
        {
            return;
        }

        Vector3 constrainedDirection = ClimbAnkleOrientation.ConstrainToeDirection(
            armature.LogicalNodes[kneeIndex].ModelMatrix.Translation,
            anklePosition,
            neutralToe,
            desiredToe,
            ankleEnvelope.HardMinimumDegrees,
            ankleEnvelope.HardMaximumDegrees);
        RotateBoneToward(ankle, toe, anklePosition + (constrainedDirection * toeLength));
    }

    private void SolveTwoBoneLimb(
        string endEffectorBone,
        IReadOnlyList<string> jointBones,
        Vector3 target,
        Vector3 preferredBendDirection,
        SmplxJointEnvelope flexionEnvelope,
        bool keepMiddleAboveRoot = false)
    {
        if (jointBones.Count != 2
            || !nodeByName.TryGetValue(jointBones[0], out int upperIndex)
            || !nodeByName.TryGetValue(jointBones[1], out int lowerIndex)
            || !nodeByName.TryGetValue(endEffectorBone, out int endEffectorIndex))
        {
            return;
        }

        NodeInstance upper = armature.LogicalNodes[upperIndex];
        NodeInstance lower = armature.LogicalNodes[lowerIndex];
        NodeInstance endEffector = armature.LogicalNodes[endEffectorIndex];
        Vector3 root = upper.ModelMatrix.Translation;
        Vector3 middle = lower.ModelMatrix.Translation;
        Vector3 end = endEffector.ModelMatrix.Translation;
        float upperLength = Vector3.Distance(root, middle);
        float lowerLength = Vector3.Distance(middle, end);
        Vector3 rootToTarget = target - root;
        float requestedDistance = rootToTarget.Length();
        if (upperLength < 0.0001f || lowerLength < 0.0001f || requestedDistance < 0.0001f)
        {
            return;
        }

        float minimumDistance = MathF.Abs(upperLength - lowerLength) + 0.0001f;
        float maximumDistance = (upperLength + lowerLength) - 0.0001f;
        float hardMinimumFlexion = Math.Clamp(flexionEnvelope.HardMinimumDegrees, 0f, 179.9f);
        float hardMaximumFlexion = Math.Clamp(
            flexionEnvelope.HardMaximumDegrees,
            hardMinimumFlexion,
            179.9f);
        float constrainedMinimumDistance = MathF.Max(
            minimumDistance,
            EndDistanceForFlexion(upperLength, lowerLength, hardMaximumFlexion));
        float constrainedMaximumDistance = MathF.Min(
            maximumDistance,
            EndDistanceForFlexion(upperLength, lowerLength, hardMinimumFlexion));
        float solvedDistance = Math.Clamp(requestedDistance, constrainedMinimumDistance, constrainedMaximumDistance);
        Vector3 direction = rootToTarget / requestedDistance;
        Vector3 bendDirection = preferredBendDirection - (direction * Vector3.Dot(preferredBendDirection, direction));
        if (bendDirection.LengthSquared() < 0.0001f)
        {
            bendDirection = MathF.Abs(direction.X) < 0.9f
                ? Vector3.Cross(direction, Vector3.UnitX)
                : Vector3.Cross(direction, Vector3.UnitY);
        }

        bendDirection = Vector3.Normalize(bendDirection);
        float alongTarget = ((upperLength * upperLength) - (lowerLength * lowerLength) + (solvedDistance * solvedDistance))
            / (2f * solvedDistance);
        float bendHeight = MathF.Sqrt(MathF.Max(0f, (upperLength * upperLength) - (alongTarget * alongTarget)));
        Vector3 desiredMiddle = root + (direction * alongTarget) + (bendDirection * bendHeight);
        float minimumUpwardMiddleY = ((root.Y + target.Y) * 0.5f) + 0.02f;
        if (keepMiddleAboveRoot && target.Y >= root.Y && desiredMiddle.Y < minimumUpwardMiddleY)
        {
            Vector3 upwardBend = Vector3.UnitY - (direction * Vector3.Dot(Vector3.UnitY, direction));
            if (upwardBend.LengthSquared() > 0.0001f)
            {
                // For a high foot, use the highest point on the valid two-bone knee circle when the outward pole
                // would fold the knee below the hip-foot midpoint. A low foot is different: its knee must be allowed
                // below the hip while the leg extends and lifts the pelvis.
                Vector3 upwardMiddle = root
                    + (direction * alongTarget)
                    + (Vector3.Normalize(upwardBend) * bendHeight);
                if (upwardMiddle.Y > desiredMiddle.Y)
                {
                    desiredMiddle = upwardMiddle;
                }
            }
        }

        RotateBoneToward(upper, lower, desiredMiddle);
        // The analytic upper-bone rotation owns the elbow/knee plane. The cleanup is deliberately limited to the
        // lower bone: rotating the upper bone again erased the pole constraint and pulled both elbows inward.
        RotateBoneToward(lower, endEffector, target);
        SolveLowerLimbCcd(endEffector, lower, target);
    }

    private static float EndDistanceForFlexion(float upperLength, float lowerLength, float flexionDegrees)
    {
        float flexionRadians = flexionDegrees * (MathF.PI / 180f);
        return MathF.Sqrt(MathF.Max(
            0f,
            (upperLength * upperLength)
            + (lowerLength * lowerLength)
            + (2f * upperLength * lowerLength * MathF.Cos(flexionRadians))));
    }

    private static void SolveLowerLimbCcd(NodeInstance endEffector, NodeInstance lower, Vector3 target)
    {
        const int iterations = 6;
        const float toleranceSquared = 0.0009f;
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            if (Vector3.DistanceSquared(endEffector.ModelMatrix.Translation, target) <= toleranceSquared)
            {
                break;
            }

            Vector3 pivot = lower.ModelMatrix.Translation;
            Vector3 from = endEffector.ModelMatrix.Translation - pivot;
            Vector3 to = target - pivot;
            if (from.LengthSquared() < 0.000001f || to.LengthSquared() < 0.000001f)
            {
                break;
            }

            ApplyModelSpaceRotation(lower, pivot, CreateFromToRotation(from, to));
        }
    }

    private static void RotateBoneToward(NodeInstance joint, NodeInstance child, Vector3 target)
    {
        Vector3 pivot = joint.ModelMatrix.Translation;
        Vector3 from = child.ModelMatrix.Translation - pivot;
        Vector3 to = target - pivot;
        if (from.LengthSquared() < 0.000001f || to.LengthSquared() < 0.000001f)
        {
            return;
        }

        ApplyModelSpaceRotation(joint, pivot, CreateFromToRotation(from, to));
    }

    private static void ApplyModelSpaceRotation(NodeInstance node, Vector3 pivot, Quaternion rotation)
    {
        if (rotation == Quaternion.Identity)
        {
            return;
        }

        Matrix4x4 parentModel = node.VisualParent?.ModelMatrix ?? Matrix4x4.Identity;
        if (!Matrix4x4.Invert(parentModel, out Matrix4x4 inverseParent))
        {
            return;
        }

        Matrix4x4 rotationAroundPivot =
            Matrix4x4.CreateTranslation(-pivot)
            * Matrix4x4.CreateFromQuaternion(rotation)
            * Matrix4x4.CreateTranslation(pivot);
        node.LocalMatrix = node.LocalMatrix * parentModel * rotationAroundPivot * inverseParent;
    }

    private static Quaternion CreateFromToRotation(Vector3 from, Vector3 to)
    {
        Vector3 unitFrom = Vector3.Normalize(from);
        Vector3 unitTo = Vector3.Normalize(to);
        float dot = Math.Clamp(Vector3.Dot(unitFrom, unitTo), -1f, 1f);
        if (dot > 0.99999f)
        {
            return Quaternion.Identity;
        }

        if (dot < -0.99999f)
        {
            Vector3 axis = MathF.Abs(unitFrom.X) < 0.9f
                ? Vector3.Normalize(Vector3.Cross(unitFrom, Vector3.UnitX))
                : Vector3.Normalize(Vector3.Cross(unitFrom, Vector3.UnitY));
            return Quaternion.CreateFromAxisAngle(axis, MathF.PI);
        }

        Vector3 cross = Vector3.Cross(unitFrom, unitTo);
        return Quaternion.Normalize(new Quaternion(cross, 1f + dot));
    }

    private string? ResolveBone(string canonicalName, params string[] candidates)
    {
        foreach (string candidate in candidates.Prepend(canonicalName))
        {
            if (nodeByName.ContainsKey(candidate))
            {
                AddAlias(canonicalName, candidate);
                return candidate;
            }
        }

        return null;
    }

    private string[] ResolveChain(string[] canonicalChain, params string[][] candidateChains)
    {
        foreach (string[] chain in candidateChains.Prepend(canonicalChain))
        {
            if (chain.Length == 2 && chain.All(nodeByName.ContainsKey))
            {
                return chain;
            }
        }

        return [];
    }

    private void AddAlias(string alias, string target)
    {
        if (!string.IsNullOrWhiteSpace(target) && nodeByName.ContainsKey(target))
        {
            boneAliases[alias] = target;
        }
    }

    private bool HasAllBones(params string[] boneNames) => boneNames.All(nodeByName.ContainsKey);

    private Vector3 GetRequiredBonePosition(string boneName) => GetBonePosition(boneName)
        ?? throw new InvalidOperationException($"The model does not contain the required bone '{boneName}'.");

    private Vector3 GetOptionalBonePosition(string boneName, string fallbackBoneName) =>
        string.IsNullOrWhiteSpace(boneName) ? GetRequiredBonePosition(fallbackBoneName) : GetRequiredBonePosition(boneName);

    private static uint[] SequentialIndices(int count)
    {
        var indices = new uint[count];
        for (int index = 0; index < count; index++)
        {
            indices[index] = (uint)index;
        }

        return indices;
    }

    public readonly record struct ContactTargets(
        Vector3 LeftHand,
        Vector3 RightHand,
        Vector3 LeftFoot,
        Vector3 RightFoot,
        Vector3 LeftToe,
        Vector3 RightToe);

    public readonly record struct HandGripPositions(
        Vector3 LeftWrist,
        Vector3 LeftIndex,
        Vector3 LeftPinky,
        Vector3 RightWrist,
        Vector3 RightIndex,
        Vector3 RightPinky);

    public readonly record struct LegJointPositions(
        Vector3 LeftHip,
        Vector3 LeftKnee,
        Vector3 LeftFoot,
        Vector3 RightHip,
        Vector3 RightKnee,
        Vector3 RightFoot);

    public readonly record struct FootOrientationPositions(
        Vector3 LeftKnee,
        Vector3 LeftAnkle,
        Vector3 LeftToe,
        Vector3 RightKnee,
        Vector3 RightAnkle,
        Vector3 RightToe);

    public readonly record struct ArmJointPositions(
        Vector3 LeftShoulder,
        Vector3 LeftElbow,
        Vector3 LeftHand,
        Vector3 RightShoulder,
        Vector3 RightElbow,
        Vector3 RightHand);
}
