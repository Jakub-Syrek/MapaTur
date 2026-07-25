using System;
using System.IO;
using System.Linq;
using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour of <see cref="SkinnedModel"/>: the glTF rigged-model loader + CPU skinning engine behind the ridden
/// dragon. Exercised against a real rigged + animated glTF sample model (Khronos "Fox"), so the skinning math is
/// proven offline (no GL): the model loads its meshes + animations, and posing at different times moves the
/// skinned vertices — the whole point of skeletal animation.
/// </summary>
public sealed class SkinnedModelTests
{
    private static string FoxPath =>
        Path.Combine(AppContext.BaseDirectory, "TestData", "Fox.glb");

    private static SkinnedModel LoadFox() => SkinnedModel.Load(FoxPath);

    [Fact]
    public void Loads_MeshesAndAnimations_FromTheRiggedModel()
    {
        var model = LoadFox();

        model.Primitives.Should().NotBeEmpty("the model has drawable geometry");
        model.Primitives.Should().OnlyContain(p => p.BindPositions.Length > 0 && p.Indices.Length > 0);
        model.Animations.Should().NotBeEmpty("the Fox is animated (Survey/Walk/Run)");
        model.Animations.Should().OnlyContain(a => a.Duration > 0f);
    }

    [Fact]
    public void Pose_WritesFiniteSkinnedVertices()
    {
        var model = LoadFox();

        model.Pose(animationIndex: 0, seconds: 0.25f);

        foreach (SkinnedModel.Primitive p in model.Primitives)
        {
            p.PosedPositions.Should().HaveCount(p.BindPositions.Length);
            p.PosedPositions.Should().OnlyContain(v => IsFinite(v), "skinned positions must be finite");
            p.PosedNormals.Should().OnlyContain(v => IsFinite(v));
        }
    }

    [Fact]
    public void Pose_AtDifferentTimes_MovesTheSkinnedVertices()
    {
        var model = LoadFox();
        // Pick the longest animation so two sampled times are clearly different poses.
        int anim = model.Animations
            .Select((a, i) => (a.Duration, i))
            .OrderByDescending(t => t.Duration)
            .First().i;
        float duration = model.Animations[anim].Duration;

        model.Pose(anim, seconds: 0f);
        Vector3[] atStart = SnapshotFirstPrimitive(model);

        model.Pose(anim, seconds: duration * 0.5f);
        Vector3[] atMid = SnapshotFirstPrimitive(model);

        float maxShift = atStart.Zip(atMid, (a, b) => (a - b).Length()).Max();
        maxShift.Should().BeGreaterThan(0.001f, "posing the animation at a different time must move skinned vertices");
    }

    [Fact]
    public void Pose_HoldingTheSameTime_IsDeterministic()
    {
        var model = LoadFox();

        model.Pose(0, 0.3f);
        Vector3[] first = SnapshotFirstPrimitive(model);
        model.Pose(0, 0.3f);
        Vector3[] second = SnapshotFirstPrimitive(model);

        first.Zip(second, (a, b) => (a - b).Length()).Max()
            .Should().BeLessThan(1e-5f, "the same animation time reproduces the same pose");
    }

    [Fact]
    public void RotatingBonesProcedurally_DeformsTheSkinnedMesh()
    {
        // The dragon has no baked animation — we drive the skeleton ourselves. This proves the mechanism:
        // resetting to bind then rotating the rig's bones must move the skinned vertices (skin matrices update).
        var model = LoadFox();

        model.ResetPose();
        model.Skin();
        Vector3[] bind = SnapshotFirstPrimitive(model);

        var delta = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.4f);
        model.ResetPose();
        int applied = model.BoneNames.Count(b => model.RotateBone(b, delta));
        model.Skin();
        Vector3[] posed = SnapshotFirstPrimitive(model);

        applied.Should().BeGreaterThan(0, "the rig has named bones to rotate");
        bind.Zip(posed, (a, c) => (a - c).Length()).Max()
            .Should().BeGreaterThan(0.001f, "rotating the rig's bones must deform the skinned mesh");
    }

    [Fact]
    public void PoseBlend_AtWeightZero_MatchesPoseA()
    {
        var model = LoadFox();
        int b = model.Animations.Count > 1 ? 1 : 0;
        model.Pose(0, 0.2f);
        Vector3[] a = SnapshotFirstPrimitive(model);

        model.PoseBlend(0, 0.2f, b, 0.3f, 0f);

        a.Zip(SnapshotFirstPrimitive(model), (x, y) => (x - y).Length()).Max()
            .Should().BeLessThan(1e-4f, "weight 0 is fully pose A");
    }

    [Fact]
    public void PoseBlend_AtWeightOne_MatchesPoseB()
    {
        var model = LoadFox();
        int b = model.Animations.Count > 1 ? 1 : 0;
        model.Pose(b, 0.3f);
        Vector3[] poseB = SnapshotFirstPrimitive(model);

        model.PoseBlend(0, 0.2f, b, 0.3f, 1f);

        poseB.Zip(SnapshotFirstPrimitive(model), (x, y) => (x - y).Length()).Max()
            .Should().BeLessThan(1e-4f, "weight 1 is fully pose B");
    }

    [Fact]
    public void PoseBlend_AtHalf_LiesBetweenTheTwoPoses()
    {
        var model = LoadFox();
        int b = model.Animations.Count > 1 ? 1 : 0;
        model.Pose(0, 0.2f);
        Vector3[] poseA = SnapshotFirstPrimitive(model);
        model.Pose(b, 0.3f);
        Vector3[] poseB = SnapshotFirstPrimitive(model);

        model.PoseBlend(0, 0.2f, b, 0.3f, 0.5f);
        Vector3[] mid = SnapshotFirstPrimitive(model);

        poseA.Zip(poseB, (x, y) => (x - y).Length()).Max()
            .Should().BeGreaterThan(0.01f, "the two clips are distinct poses");
        for (int i = 0; i < mid.Length; i++)
        {
            float ab = (poseA[i] - poseB[i]).Length();
            (mid[i] - poseA[i]).Length().Should().BeLessThanOrEqualTo(ab + 1e-3f, "the blend lies between the endpoints");
            (mid[i] - poseB[i]).Length().Should().BeLessThanOrEqualTo(ab + 1e-3f);
        }
    }

    private static Vector3[] SnapshotFirstPrimitive(SkinnedModel model) =>
        model.Primitives[0].PosedPositions.ToArray();

    private static bool IsFinite(Vector3 v) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}