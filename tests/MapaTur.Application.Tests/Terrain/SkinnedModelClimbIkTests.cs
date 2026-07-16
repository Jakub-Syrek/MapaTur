using System.Numerics;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Guards for the IK primitives under <see cref="ClimberRigKinematics"/>. These exist because the
/// runtime's own ModelMatrix caches lazily and can return STALE descendant positions depending on the
/// read order after a LocalMatrix write — the strict forward-kinematics path must never do that.
/// </summary>
public sealed class SkinnedModelClimbIkTests
{
    private static SkinnedModel LoadHiker() =>
        SkinnedModel.Load(Path.Combine(AppContext.BaseDirectory, "TestData", "hiker.glb"));

    [Fact]
    public void RotateBoneModelSpace_should_move_the_subtree_rigidly()
    {
        SkinnedModel model = LoadHiker();
        model.ResetPose();
        Vector3 shoulder = model.GetBonePosedPositionStrict("upperarm.l")!.Value;
        Vector3 wristBefore = model.GetBonePosedPositionStrict("wrist.l")!.Value;

        model.RotateBoneModelSpace("upperarm.l", Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f));

        Vector3 wristAfter = model.GetBonePosedPositionStrict("wrist.l")!.Value;
        Vector3 delta = wristBefore - shoulder;
        Vector3 expected = shoulder + new Vector3(-delta.Y, delta.X, delta.Z);
        Assert.True(
            Vector3.Distance(wristAfter, expected) < 0.001f,
            $"expected rigid rotation to {expected}, got {wristAfter}");
    }

    [Fact]
    public void Strict_positions_should_stay_fresh_regardless_of_read_order()
    {
        // The historical failure: write parent local, read middle bone, THEN read its child -> child
        // came back at its BIND position and the forearm "stretched". Strict FK must be order-blind.
        SkinnedModel model = LoadHiker();
        model.ResetPose();
        Vector3 elbowBind = model.GetBonePosedPositionStrict("lowerarm.l")!.Value;
        Vector3 wristBind = model.GetBonePosedPositionStrict("wrist.l")!.Value;
        float forearmLength = Vector3.Distance(elbowBind, wristBind);

        model.RotateBoneModelSpace("upperarm.l", Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.7f));
        Vector3 elbow = model.GetBonePosedPositionStrict("lowerarm.l")!.Value;   // middle FIRST on purpose
        Vector3 wrist = model.GetBonePosedPositionStrict("wrist.l")!.Value;

        Assert.NotEqual(wristBind, wrist);
        Assert.True(
            MathF.Abs(Vector3.Distance(elbow, wrist) - forearmLength) < 0.001f,
            $"forearm stretched: {Vector3.Distance(elbow, wrist):F4} vs bind {forearmLength:F4}");
    }

    [Fact]
    public void Repeated_rotations_should_not_stretch_the_chain()
    {
        SkinnedModel model = LoadHiker();
        model.ResetPose();
        Vector3 shoulder = model.GetBonePosedPositionStrict("upperarm.l")!.Value;
        Vector3 elbowBind = model.GetBonePosedPositionStrict("lowerarm.l")!.Value;
        Vector3 wristBind = model.GetBonePosedPositionStrict("wrist.l")!.Value;
        float upperLength = Vector3.Distance(shoulder, elbowBind);
        float forearmLength = Vector3.Distance(elbowBind, wristBind);

        for (int i = 0; i < 40; i++)
        {
            model.RotateBoneModelSpace("upperarm.l", Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.13f));
            model.RotateBoneModelSpace("lowerarm.l", Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.09f));
        }

        Vector3 s = model.GetBonePosedPositionStrict("upperarm.l")!.Value;
        Vector3 e = model.GetBonePosedPositionStrict("lowerarm.l")!.Value;
        Vector3 w = model.GetBonePosedPositionStrict("wrist.l")!.Value;
        Assert.True(MathF.Abs(Vector3.Distance(s, e) - upperLength) < 0.002f, "upper arm length drifted");
        Assert.True(MathF.Abs(Vector3.Distance(e, w) - forearmLength) < 0.002f, "forearm length drifted");
    }
}