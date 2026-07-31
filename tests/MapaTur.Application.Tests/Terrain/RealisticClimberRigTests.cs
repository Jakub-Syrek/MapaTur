using System.Numerics;

using MapaTur.Application.Terrain;
using MapaTur.Climbing;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Gate on the LICENSED realistic climber (local-only data): the taken-over rig must reach the four
/// contacts AND the skinned mesh must stay an articulated body — never the crumpled ball that an
/// inconsistent pose/skin path produces. Skipped silently where the model file is absent (CI).
/// </summary>
public sealed class RealisticClimberRigTests
{
    private static string? ModelPath()
    {
        string? env = Environment.GetEnvironmentVariable("MAPATUR_CLIMBER_MODEL");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            return env;
        }

        string canonical = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "User Name", "com.companyname.mapatur.app", "Data", "models", "RockClimber_Realistic.glb");
        return File.Exists(canonical) ? canonical : null;
    }

    private static TrianglePatchClimbSurface NorthFacingWall()
    {
        Vector3[] vertices = [new(-3f, 0f, -1f), new(3f, 0f, -1f), new(3f, 0f, 5f), new(-3f, 0f, 5f)];
        int[] indices = [0, 3, 1, 1, 3, 2];
        return new TrianglePatchClimbSurface(new ClimbSurfacePatch("test-wall", vertices, indices, []));
    }

    private static Dictionary<ClimbLimb, LimbContact> FourContacts(RealisticClimberRig rig, Vector3 pelvis)
    {
        Vector3 outward = new(0f, 1f, 0f);
        Vector3 leftShoulder = pelvis + rig.LeftShoulderOffsetMeters;
        Vector3 leftHip = pelvis + rig.LeftHipOffsetMeters;
        float handUp = 0.45f * rig.ArmReachMeters;
        float footDown = 0.60f * rig.LegReachMeters;

        ClimbHold Hand(string id, float side) => new(
            id, new Vector3(side * (MathF.Abs(leftShoulder.X) + 0.06f), 0f, leftShoulder.Z + handUp), outward, 0.9f);
        ClimbHold Foot(string id, float side) => new(
            id, new Vector3(side * (MathF.Abs(leftHip.X) + 0.04f), 0f, leftHip.Z - footDown), outward, 0.9f, ClimbHoldType.FootEdge);

        float leftSide = MathF.Sign(leftShoulder.X);
        leftSide = leftSide == 0f ? 1f : leftSide;
        return new Dictionary<ClimbLimb, LimbContact>
        {
            [ClimbLimb.LeftHand] = new(ClimbLimb.LeftHand, Hand("lh", leftSide), 0f),
            [ClimbLimb.RightHand] = new(ClimbLimb.RightHand, Hand("rh", -leftSide), 0f),
            [ClimbLimb.LeftFoot] = new(ClimbLimb.LeftFoot, Foot("lf", leftSide), 0f),
            [ClimbLimb.RightFoot] = new(ClimbLimb.RightFoot, Foot("rf", -leftSide), 0f)
        };
    }

    [Fact]
    public void Realistic_rig_should_reach_contacts_and_stay_articulated()
    {
        if (ModelPath() is not { } path)
        {
            return; // licensed model absent (CI) — nothing to verify here
        }

        ClimberSkinnedModel model = ClimberSkinnedModel.Load(path);
        Assert.True(model.SupportsContactIk, "the realistic climber must expose the full IK bone set");
        var rig = new RealisticClimberRig(model, 1.85f);
        var seed = new ClimbWholeBodyRootPose(new Vector3(0f, 0.40f, 1.2f), 0f, 0f, 0f);
        rig.SetClimbContext(NorthFacingWall(), FourContacts(rig, seed.Pelvis), ClimbWorld.Gravity);

        ClimbWholeBodyPoseSample sample = rig.Evaluate(seed);

        Assert.True(
            Vector3.Distance(sample.PoseLandmarks.Pelvis, seed.Pelvis) < 0.06f,
            $"pelvis {sample.PoseLandmarks.Pelvis} should sit at the root {seed.Pelvis}");
        foreach (ClimbWholeBodyContactState contact in sample.Contacts)
        {
            float error = Vector3.Distance(contact.ActualPosition, contact.TargetPosition);
            Assert.True(
                error <= 0.12f,
                $"{contact.Limb} missed its hold by {error:F3} m (target {contact.TargetPosition}, actual {contact.ActualPosition})");
        }

        // The crumple guard: after skinning, the posed mesh must still span an articulated body.
        rig.Skin();
        (Vector3 bindMin, Vector3 bindMax) = (model.BindBoundsMin, model.BindBoundsMax);
        (Vector3 posedMin, Vector3 posedMax) = model.GetPosedBounds();
        float bindHeight = bindMax.Y - bindMin.Y;
        float posedHeight = posedMax.Y - posedMin.Y;
        Assert.True(
            posedHeight > 0.55f * bindHeight,
            $"posed mesh collapsed: height {posedHeight:F2} vs bind {bindHeight:F2} (the 'ball' regression)");
        Assert.True(
            (posedMax - posedMin).Length() < 2.5f * (bindMax - bindMin).Length(),
            "posed mesh exploded beyond plausible bounds");
    }

    [Fact]
    public void WholeBody_solver_should_settle_on_the_realistic_rig()
    {
        if (ModelPath() is not { } path)
        {
            return; // licensed model absent (CI)
        }

        ClimberSkinnedModel model = ClimberSkinnedModel.Load(path);
        var rig = new RealisticClimberRig(model, 1.85f);
        var seed = new ClimbWholeBodyRootPose(new Vector3(0f, 0.40f, 1.2f), 0f, 0f, 0f);
        TrianglePatchClimbSurface wall = NorthFacingWall();
        rig.SetClimbContext(wall, FourContacts(rig, seed.Pelvis), ClimbWorld.Gravity);
        var solver = new SequentialWholeBodyClimbSolver(
            SmplxPosePriorProfile.CreateBootstrap(),
            new ClimbMechanicsConfiguration { Gravity = ClimbWorld.Gravity });
        var request = new ClimbWholeBodySolveRequest
        {
            ReferencePose = seed,
            SeedPose = seed,
            SurfaceFrame = wall.SampleSurface(seed.Pelvis, ClimbWorld.Gravity),
            CharacteristicLengthMeters = 1.85f,
            MaximumContactErrorMeters = 0.12f
        };

        ClimbWholeBodySolveResult result = solver.Solve(request, rig);

        Assert.True(result.IsFeasible, $"whole-body solve infeasible on the realistic rig: cost {result.Cost}");
        Assert.True(
            result.MaximumContactErrorMeters <= 0.12f,
            $"max contact error {result.MaximumContactErrorMeters:F3} m");
    }
}