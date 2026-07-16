using System.Collections.ObjectModel;
using System.Numerics;
using System.Text.Json;

namespace MapaTur.Climbing;

/// <summary>
/// Geometry-only features derived from the 22 body joints shared by SMPL-X and VPoser.
/// The feature representation deliberately contains no renderer or rig bone names, so a MapaTur
/// character adapter can provide the same landmarks as the standalone viewer.
/// </summary>
public static class SmplxPoseFeatureNames
{
    public const string LeftElbowFlexion = "leftElbowFlexion";
    public const string RightElbowFlexion = "rightElbowFlexion";
    public const string LeftKneeFlexion = "leftKneeFlexion";
    public const string RightKneeFlexion = "rightKneeFlexion";
    public const string LeftAnkleAngle = "leftAnkleAngle";
    public const string RightAnkleAngle = "rightAnkleAngle";
    public const string LeftShoulderTorsoAngle = "leftShoulderTorsoAngle";
    public const string RightShoulderTorsoAngle = "rightShoulderTorsoAngle";
    public const string LeftHipTorsoAngle = "leftHipTorsoAngle";
    public const string RightHipTorsoAngle = "rightHipTorsoAngle";

    public static IReadOnlyList<string> Required { get; } =
    [
        LeftElbowFlexion,
        RightElbowFlexion,
        LeftKneeFlexion,
        RightKneeFlexion,
        LeftAnkleAngle,
        RightAnkleAngle,
        LeftShoulderTorsoAngle,
        RightShoulderTorsoAngle,
        LeftHipTorsoAngle,
        RightHipTorsoAngle
    ];
}

/// <summary>
/// A learned typical range and a separate hard anatomical envelope. The learned range is a soft prior;
/// it must never reject an uncommon but valid climbing move on its own. The hard range is enforced by IK.
/// </summary>
public sealed record SmplxJointEnvelope(
    float TypicalMinimumDegrees,
    float TypicalMaximumDegrees,
    float HardMinimumDegrees,
    float HardMaximumDegrees);

/// <summary>
/// World-space landmarks following the SMPL-X body topology. LeftFoot/RightFoot are the distal foot joints,
/// while LeftAnkle/RightAnkle are the leg-chain end joints.
/// </summary>
public sealed record SmplxPoseLandmarks(
    Vector3 Pelvis,
    Vector3 Neck,
    Vector3 LeftShoulder,
    Vector3 LeftElbow,
    Vector3 LeftWrist,
    Vector3 RightShoulder,
    Vector3 RightElbow,
    Vector3 RightWrist,
    Vector3 LeftHip,
    Vector3 LeftKnee,
    Vector3 LeftAnkle,
    Vector3 LeftFoot,
    Vector3 RightHip,
    Vector3 RightKnee,
    Vector3 RightAnkle,
    Vector3 RightFoot);

public sealed record SmplxPoseViolation(
    string Feature,
    float ValueDegrees,
    float HardMinimumDegrees,
    float HardMaximumDegrees);

public sealed record SmplxPoseAssessment(
    bool IsInsideHardLimits,
    float TypicalityScore,
    float SoftPenalty,
    IReadOnlyDictionary<string, float> MeasurementsDegrees,
    IReadOnlyList<SmplxPoseViolation> HardViolations);

/// <summary>
/// Serializable boundary between the official Python SMPL-X/VPoser tooling and the realtime C# solver.
/// Model weights remain outside the application; the exporter writes only derived joint envelopes and
/// neutral segment lengths, which keeps the runtime small and deterministic.
/// </summary>
public sealed class SmplxPosePriorProfile
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string ModelType { get; init; } = "smplx";

    public string Gender { get; init; } = "neutral";

    public string SourceKind { get; init; } = "bootstrap";

    public string Source { get; init; } = string.Empty;

    public int SampleCount { get; init; }

    public Dictionary<string, SmplxJointEnvelope> Features { get; init; } = new(StringComparer.Ordinal);

    public Dictionary<string, float> RestSegmentLengthsMeters { get; init; } = new(StringComparer.Ordinal);

    public bool IsLearned => string.Equals(SourceKind, "vposer", StringComparison.OrdinalIgnoreCase);

    public static SmplxPosePriorProfile Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using FileStream stream = File.OpenRead(path);
        SmplxPosePriorProfile profile = JsonSerializer.Deserialize<SmplxPosePriorProfile>(
                stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException($"SMPL-X profile '{path}' is empty.");
        profile.Validate(path);
        return profile;
    }

    /// <summary>
    /// Conservative limits used before the user supplies licensed SMPL-X/VPoser weights to the offline exporter.
    /// These are intentionally labelled bootstrap: they use the SMPL-X feature topology but are not claimed to be
    /// learned from VPoser. Replacing them requires only CLIMBER3D_SMPLX_PROFILE, not a rebuild.
    /// </summary>
    public static SmplxPosePriorProfile CreateBootstrap() => new()
    {
        SourceKind = "bootstrap",
        Source = "Conservative SMPL-X-compatible bootstrap; replace with tools/smplx/export_smplx_profile.py output.",
        Features = new Dictionary<string, SmplxJointEnvelope>(StringComparer.Ordinal)
        {
            [SmplxPoseFeatureNames.LeftElbowFlexion] = new(0f, 135f, 0f, 150f),
            [SmplxPoseFeatureNames.RightElbowFlexion] = new(0f, 135f, 0f, 150f),
            [SmplxPoseFeatureNames.LeftKneeFlexion] = new(5f, 145f, 0f, 155f),
            [SmplxPoseFeatureNames.RightKneeFlexion] = new(5f, 145f, 0f, 155f),
            [SmplxPoseFeatureNames.LeftAnkleAngle] = new(75f, 130f, 70f, 135f),
            [SmplxPoseFeatureNames.RightAnkleAngle] = new(75f, 130f, 70f, 135f),
            [SmplxPoseFeatureNames.LeftShoulderTorsoAngle] = new(8f, 172f, 0f, 180f),
            [SmplxPoseFeatureNames.RightShoulderTorsoAngle] = new(8f, 172f, 0f, 180f),
            [SmplxPoseFeatureNames.LeftHipTorsoAngle] = new(0f, 165f, 0f, 180f),
            [SmplxPoseFeatureNames.RightHipTorsoAngle] = new(0f, 165f, 0f, 180f)
        }
    };

    public SmplxJointEnvelope GetEnvelope(string feature)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feature);
        return Features.TryGetValue(feature, out SmplxJointEnvelope? envelope)
            ? envelope
            : throw new KeyNotFoundException($"SMPL-X profile does not define '{feature}'.");
    }

    public SmplxPoseAssessment Assess(SmplxPoseLandmarks pose)
    {
        ArgumentNullException.ThrowIfNull(pose);
        IReadOnlyDictionary<string, float> measurements = Measure(pose);
        var violations = new List<SmplxPoseViolation>();
        float softPenalty = 0f;
        foreach ((string feature, float value) in measurements)
        {
            SmplxJointEnvelope envelope = GetEnvelope(feature);
            softPenalty += OutsidePenalty(value, envelope.TypicalMinimumDegrees, envelope.TypicalMaximumDegrees);
            if (value < envelope.HardMinimumDegrees - 0.25f || value > envelope.HardMaximumDegrees + 0.25f)
            {
                violations.Add(new SmplxPoseViolation(
                    feature,
                    value,
                    envelope.HardMinimumDegrees,
                    envelope.HardMaximumDegrees));
            }
        }

        float typicality = MathF.Exp(-softPenalty / Math.Max(1, measurements.Count));
        return new SmplxPoseAssessment(
            violations.Count == 0,
            typicality,
            softPenalty,
            measurements,
            violations);
    }

    public static IReadOnlyDictionary<string, float> Measure(SmplxPoseLandmarks pose)
    {
        ArgumentNullException.ThrowIfNull(pose);
        Vector3 torsoUp = pose.Neck - pose.Pelvis;
        Vector3 torsoDown = -torsoUp;
        var measurements = new Dictionary<string, float>(StringComparer.Ordinal)
        {
            [SmplxPoseFeatureNames.LeftElbowFlexion] = FlexionDegrees(pose.LeftShoulder, pose.LeftElbow, pose.LeftWrist),
            [SmplxPoseFeatureNames.RightElbowFlexion] = FlexionDegrees(pose.RightShoulder, pose.RightElbow, pose.RightWrist),
            [SmplxPoseFeatureNames.LeftKneeFlexion] = FlexionDegrees(pose.LeftHip, pose.LeftKnee, pose.LeftAnkle),
            [SmplxPoseFeatureNames.RightKneeFlexion] = FlexionDegrees(pose.RightHip, pose.RightKnee, pose.RightAnkle),
            [SmplxPoseFeatureNames.LeftAnkleAngle] = JointAngleDegrees(pose.LeftKnee, pose.LeftAnkle, pose.LeftFoot),
            [SmplxPoseFeatureNames.RightAnkleAngle] = JointAngleDegrees(pose.RightKnee, pose.RightAnkle, pose.RightFoot),
            [SmplxPoseFeatureNames.LeftShoulderTorsoAngle] = AngleDegrees(torsoUp, pose.LeftElbow - pose.LeftShoulder),
            [SmplxPoseFeatureNames.RightShoulderTorsoAngle] = AngleDegrees(torsoUp, pose.RightElbow - pose.RightShoulder),
            [SmplxPoseFeatureNames.LeftHipTorsoAngle] = AngleDegrees(torsoDown, pose.LeftKnee - pose.LeftHip),
            [SmplxPoseFeatureNames.RightHipTorsoAngle] = AngleDegrees(torsoDown, pose.RightKnee - pose.RightHip)
        };
        return new ReadOnlyDictionary<string, float>(measurements);
    }

    private void Validate(string sourcePath)
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"SMPL-X profile '{sourcePath}' uses schema {SchemaVersion}; expected {CurrentSchemaVersion}.");
        }

        if (!string.Equals(ModelType, "smplx", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"SMPL-X profile '{sourcePath}' has modelType '{ModelType}'.");
        }

        foreach (string feature in SmplxPoseFeatureNames.Required)
        {
            SmplxJointEnvelope envelope = GetEnvelope(feature);
            bool ordered = float.IsFinite(envelope.HardMinimumDegrees)
                && float.IsFinite(envelope.HardMaximumDegrees)
                && float.IsFinite(envelope.TypicalMinimumDegrees)
                && float.IsFinite(envelope.TypicalMaximumDegrees)
                && envelope.HardMinimumDegrees <= envelope.TypicalMinimumDegrees
                && envelope.TypicalMinimumDegrees <= envelope.TypicalMaximumDegrees
                && envelope.TypicalMaximumDegrees <= envelope.HardMaximumDegrees;
            if (!ordered)
            {
                throw new InvalidDataException($"SMPL-X feature '{feature}' has an invalid envelope.");
            }
        }
    }

    private static float FlexionDegrees(Vector3 proximal, Vector3 joint, Vector3 distal) =>
        180f - JointAngleDegrees(proximal, joint, distal);

    private static float JointAngleDegrees(Vector3 first, Vector3 joint, Vector3 second) =>
        AngleDegrees(first - joint, second - joint);

    private static float AngleDegrees(Vector3 first, Vector3 second)
    {
        float denominator = MathF.Sqrt(first.LengthSquared() * second.LengthSquared());
        if (denominator < 1e-8f)
        {
            return 0f;
        }

        float dot = Math.Clamp(Vector3.Dot(first, second) / denominator, -1f, 1f);
        return MathF.Acos(dot) * (180f / MathF.PI);
    }

    private static float OutsidePenalty(float value, float minimum, float maximum)
    {
        float width = MathF.Max(1f, maximum - minimum);
        float distance = value < minimum
            ? minimum - value
            : value > maximum
                ? value - maximum
                : 0f;
        float normalized = distance / width;
        return normalized * normalized;
    }
}