using System.Numerics;

namespace MapaTur.Climbing.Tests;

/// <summary>
/// Directional hip features ("uda 120° do tyłu" user report, 2026-07-19): the legacy HipTorsoAngle is an
/// UNSIGNED angle with a 0–180° hard envelope — it can never fire, and it cannot tell a legal high step
/// (thigh forward) from an impossible hip hyperextension (thigh backward): both measure the same number.
/// The new features are signed in the pelvis frame derived from the landmarks themselves: a sagittal
/// angle (positive forward = flexion, negative backward = extension) and an abduction angle (positive =
/// leg opening sideways in the crotch). Older external SMPL-X profiles that lack these envelopes must
/// keep loading and assessing (the new features are simply unconstrained there).
/// </summary>
public sealed class SmplxPosePriorHipTests
{
    // Canonical synthetic body: right hip on +X, torso up +Z → right-handed body frame faces +Y
    // (forward = up × right). Arms hang down (all arm/leg features that are NOT under test stay well
    // inside their envelopes, so hip assertions are isolated).
    private static SmplxPoseLandmarks Pose(Vector3 leftThighDir, Vector3 rightThighDir)
    {
        var pelvis = new Vector3(0f, 0f, 1.00f);
        var neck = new Vector3(0f, 0f, 1.55f);
        var leftHip = new Vector3(-0.10f, 0f, 0.95f);
        var rightHip = new Vector3(0.10f, 0f, 0.95f);
        var leftShoulder = new Vector3(-0.18f, 0f, 1.45f);
        var rightShoulder = new Vector3(0.18f, 0f, 1.45f);
        // Arms: slightly out and down (shoulder-torso angle ~20°), elbows a touch bent.
        var leftElbow = leftShoulder + new Vector3(-0.09f, 0f, -0.26f);
        var rightElbow = rightShoulder + new Vector3(0.09f, 0f, -0.26f);
        var leftWrist = leftElbow + new Vector3(-0.04f, -0.06f, -0.24f);
        var rightWrist = rightElbow + new Vector3(0.04f, -0.06f, -0.24f);

        Vector3 leftKnee = leftHip + (Vector3.Normalize(leftThighDir) * 0.42f);
        Vector3 rightKnee = rightHip + (Vector3.Normalize(rightThighDir) * 0.42f);
        // Shin continues mostly along the thigh with a slight bend (knee flexion small, ankle mid-range).
        Vector3 leftAnkle = leftKnee + (Vector3.Normalize(leftThighDir + new Vector3(0f, -0.25f, -0.2f)) * 0.42f);
        Vector3 rightAnkle = rightKnee + (Vector3.Normalize(rightThighDir + new Vector3(0f, -0.25f, -0.2f)) * 0.42f);
        Vector3 leftFoot = leftAnkle + (Vector3.Normalize(new Vector3(0f, -1f, 0.35f)) * 0.16f);
        Vector3 rightFoot = rightAnkle + (Vector3.Normalize(new Vector3(0f, -1f, 0.35f)) * 0.16f);

        return new SmplxPoseLandmarks(
            pelvis, neck,
            leftShoulder, leftElbow, leftWrist,
            rightShoulder, rightElbow, rightWrist,
            leftHip, leftKnee, leftAnkle, leftFoot,
            rightHip, rightKnee, rightAnkle, rightFoot);
    }

    private static readonly Vector3 StraightDown = new(0f, 0f, -1f);

    // Thigh direction rotated from straight-down by `degrees` toward the character's BACK (-Y when facing +Y).
    private static Vector3 BackwardBy(float degrees)
    {
        float radians = degrees * (MathF.PI / 180f);
        return new Vector3(0f, -MathF.Sin(radians), -MathF.Cos(radians));
    }

    // Thigh direction rotated from straight-down toward the character's FRONT (+Y).
    private static Vector3 ForwardBy(float degrees)
    {
        float radians = degrees * (MathF.PI / 180f);
        return new Vector3(0f, MathF.Sin(radians), -MathF.Cos(radians));
    }

    // Thigh direction rotated from straight-down OUTWARD for the given side (+1 = right leg, -1 = left leg).
    private static Vector3 OutwardBy(float degrees, float side)
    {
        float radians = degrees * (MathF.PI / 180f);
        return new Vector3(side * MathF.Sin(radians), 0f, -MathF.Cos(radians));
    }

    [Fact]
    public void Measure_should_report_signed_sagittal_angles_forward_positive_backward_negative()
    {
        IReadOnlyDictionary<string, float> forward = SmplxPosePriorProfile.Measure(Pose(ForwardBy(90f), ForwardBy(90f)));
        IReadOnlyDictionary<string, float> backward = SmplxPosePriorProfile.Measure(Pose(BackwardBy(120f), BackwardBy(120f)));

        Assert.InRange(forward[SmplxPoseFeatureNames.LeftHipSagittalAngle], 80f, 100f);
        Assert.InRange(forward[SmplxPoseFeatureNames.RightHipSagittalAngle], 80f, 100f);
        Assert.InRange(backward[SmplxPoseFeatureNames.LeftHipSagittalAngle], -135f, -105f);
        Assert.InRange(backward[SmplxPoseFeatureNames.RightHipSagittalAngle], -135f, -105f);
    }

    [Fact]
    public void Assess_should_reject_thighs_pointing_120_degrees_backward()
    {
        SmplxPoseAssessment assessment = SmplxPosePriorProfile.CreateBootstrap()
            .Assess(Pose(BackwardBy(120f), BackwardBy(120f)));

        Assert.False(assessment.IsInsideHardLimits);
        Assert.Contains(assessment.HardViolations, violation =>
            violation.Feature is SmplxPoseFeatureNames.LeftHipSagittalAngle
                or SmplxPoseFeatureNames.RightHipSagittalAngle);
    }

    [Fact]
    public void Assess_should_accept_a_legal_high_step_with_the_thigh_far_forward()
    {
        SmplxPoseAssessment assessment = SmplxPosePriorProfile.CreateBootstrap()
            .Assess(Pose(ForwardBy(115f), StraightDown));

        Assert.DoesNotContain(assessment.HardViolations, violation =>
            violation.Feature is SmplxPoseFeatureNames.LeftHipSagittalAngle
                or SmplxPoseFeatureNames.RightHipSagittalAngle
                or SmplxPoseFeatureNames.LeftHipAbductionAngle
                or SmplxPoseFeatureNames.RightHipAbductionAngle);
    }

    [Fact]
    public void Assess_should_accept_a_wide_stem_but_reject_an_extreme_crotch_split()
    {
        SmplxPosePriorProfile profile = SmplxPosePriorProfile.CreateBootstrap();

        SmplxPoseAssessment stem = profile.Assess(Pose(OutwardBy(55f, -1f), OutwardBy(55f, 1f)));
        SmplxPoseAssessment split = profile.Assess(Pose(OutwardBy(78f, -1f), OutwardBy(78f, 1f)));

        Assert.DoesNotContain(stem.HardViolations, violation =>
            violation.Feature is SmplxPoseFeatureNames.LeftHipAbductionAngle
                or SmplxPoseFeatureNames.RightHipAbductionAngle);
        Assert.Contains(split.HardViolations, violation =>
            violation.Feature is SmplxPoseFeatureNames.LeftHipAbductionAngle
                or SmplxPoseFeatureNames.RightHipAbductionAngle);
    }

    [Fact]
    public void Assess_should_tolerate_profiles_that_predate_the_hip_features()
    {
        // An external profile exported before these features existed: only the legacy ten envelopes.
        var legacy = new SmplxPosePriorProfile
        {
            Features = new Dictionary<string, SmplxJointEnvelope>(
                SmplxPosePriorProfile.CreateBootstrap().Features
                    .Where(pair => SmplxPoseFeatureNames.Required.Contains(pair.Key))
                    .ToDictionary(pair => pair.Key, pair => pair.Value),
                StringComparer.Ordinal)
        };

        SmplxPoseAssessment assessment = legacy.Assess(Pose(BackwardBy(120f), BackwardBy(120f)));

        Assert.DoesNotContain(assessment.HardViolations, violation =>
            violation.Feature is SmplxPoseFeatureNames.LeftHipSagittalAngle
                or SmplxPoseFeatureNames.RightHipSagittalAngle);
    }
}