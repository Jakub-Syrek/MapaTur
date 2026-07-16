using System.Numerics;

using MapaTur.Climbing;

namespace MapaTur.Climbing.Tests;

/// <summary>
/// The Climber3d PoC zigzag acceptance route (28 moves, four-phase cycle), expressed in MapaTur
/// coordinates: X-east / Y-north / Z-up, gravity (0, 0, -9.81). The demo authored the route in
/// Y-up viewer space; this fixture applies the pure rotation (x, y, z) -> (x, -z, y) so the same
/// physical route exercises the solver in the axes the terrain adapter will use.
/// </summary>
internal sealed class ZigZagRouteCorpus
{
    private ZigZagRouteCorpus(
        IReadOnlyList<ClimbHold> holds,
        ClimbState initialState,
        IReadOnlyList<CorpusMove> moves,
        ClimbRouteGeometrySummary geometry)
    {
        Holds = holds;
        InitialState = initialState;
        Moves = moves;
        Geometry = geometry;
        HoldsById = holds.ToDictionary(hold => hold.Id);
    }

    public IReadOnlyList<ClimbHold> Holds { get; }

    public Dictionary<string, ClimbHold> HoldsById { get; }

    public ClimbState InitialState { get; }

    public IReadOnlyList<CorpusMove> Moves { get; }

    public ClimbRouteGeometrySummary Geometry { get; }

    public static ZigZagRouteCorpus Create(float wallPitchDegrees)
    {
        float wallSlope = MathF.Tan(wallPitchDegrees * (MathF.PI / 180f));
        RouteStage[] stages =
        [
            Stage(-0.15f, 0.00f, -0.85f, 0.07f, 0.80f, -0.03f, -0.67f, 0.00f, 0.33f, -0.14f, false, 0),
            Stage(0.35f, 0.38f, -0.72f, 0.20f, 0.88f, -0.08f, -0.55f, 0.05f, 0.48f, -0.10f, true, 1),
            Stage(-0.35f, 0.78f, -0.95f, -0.05f, 0.68f, 0.18f, -0.62f, 0.16f, 0.42f, -0.10f, false, 2),
            Stage(0.45f, 1.17f, -0.68f, 0.25f, 0.73f, -0.08f, -0.48f, 0.20f, 0.25f, 0.05f, true, 3),
            Stage(-0.45f, 1.56f, -0.83f, -0.08f, 0.62f, 0.22f, -0.58f, 0.18f, 0.38f, -0.14f, false, 4),
            Stage(0.55f, 1.95f, -0.62f, 0.24f, 0.55f, -0.06f, -0.45f, 0.18f, 0.35f, -0.02f, true, 5),
            Stage(-0.35f, 2.34f, -0.67f, 0.10f, 1.15f, 0.12f, -0.35f, 0.12f, 0.75f, 0.29f, false, 6),
            Stage(0.45f, 2.73f, -0.70f, 0.22f, 0.75f, -0.05f, -0.85f, 0.16f, 0.52f, -0.12f, true, 7)
        ];

        List<ClimbHold> holds = [];
        List<CorpusMove> moves = [];
        var geometryStages = new List<Dictionary<ClimbLimb, Vector3>>(stages.Length);
        for (int level = 0; level < stages.Length; level++)
        {
            RouteStage stage = stages[level];
            ClimbHold upperA = CreateHold(HoldId("uppera", level), stage.LeftHand, wallSlope);
            ClimbHold upperB = CreateHold(HoldId("upperb", level), stage.RightHand, wallSlope);
            ClimbHold lowerA = CreateHold(HoldId("lowera", level), stage.LeftFoot, wallSlope);
            ClimbHold lowerB = CreateHold(HoldId("lowerb", level), stage.RightFoot, wallSlope);
            holds.AddRange([lowerA, lowerB, upperA, upperB]);
            geometryStages.Add(new Dictionary<ClimbLimb, Vector3>
            {
                [ClimbLimb.LeftHand] = upperA.Position,
                [ClimbLimb.RightHand] = upperB.Position,
                [ClimbLimb.LeftFoot] = lowerA.Position,
                [ClimbLimb.RightFoot] = lowerB.Position
            });

            if (level > 0)
            {
                ClimbLimb directionalHand = stage.MovesRight ? ClimbLimb.RightHand : ClimbLimb.LeftHand;
                ClimbLimb contralateralFoot = stage.MovesRight ? ClimbLimb.LeftFoot : ClimbLimb.RightFoot;
                ClimbLimb directionalFoot = stage.MovesRight ? ClimbLimb.RightFoot : ClimbLimb.LeftFoot;
                ClimbLimb contralateralHand = stage.MovesRight ? ClimbLimb.LeftHand : ClimbLimb.RightHand;
                CorpusMove handReach = new(directionalHand, PlannedHoldId(directionalHand, level));
                CorpusMove footSetup = new(contralateralFoot, PlannedHoldId(contralateralFoot, level));
                CorpusMove footDrive = new(directionalFoot, PlannedHoldId(directionalFoot, level));
                CorpusMove handSettle = new(contralateralHand, PlannedHoldId(contralateralHand, level));

                // Same rule as the PoC zigzag beta: establish the next foothold first when the same hand
                // would otherwise move twice in a row across a stage boundary.
                bool needsFootSetupBeforeReach = moves.Count > 0 && moves[^1].Limb == directionalHand;
                if (needsFootSetupBeforeReach)
                {
                    moves.AddRange([footSetup, handReach, footDrive, handSettle]);
                }
                else
                {
                    moves.AddRange([handReach, footSetup, footDrive, handSettle]);
                }
            }
        }

        Dictionary<ClimbLimb, Vector3> initialContacts = geometryStages[0];
        float initialPelvisX = initialContacts.Values.Average(position => position.X);
        float initialFootUp = (initialContacts[ClimbLimb.LeftFoot].Z + initialContacts[ClimbLimb.RightFoot].Z) * 0.5f;
        float initialHandUp = (initialContacts[ClimbLimb.LeftHand].Z + initialContacts[ClimbLimb.RightHand].Z) * 0.5f;
        float initialPelvisUp = (initialFootUp + initialHandUp) * 0.5f - 0.09f;
        Vector3 initialPelvis = MapPoint(
            new Vector3(initialPelvisX, initialPelvisUp, WallDepth(initialPelvisX, initialPelvisUp, wallSlope) + 0.62f));
        ClimbState initialState = ClimbState.Create(
            initialPelvis,
            [
                new LimbContact(ClimbLimb.LeftHand, holds.Single(hold => hold.Id == HoldId("uppera", 0)), 0f),
                new LimbContact(ClimbLimb.RightHand, holds.Single(hold => hold.Id == HoldId("upperb", 0)), 0f),
                new LimbContact(ClimbLimb.LeftFoot, holds.Single(hold => hold.Id == HoldId("lowera", 0)), 0f),
                new LimbContact(ClimbLimb.RightFoot, holds.Single(hold => hold.Id == HoldId("lowerb", 0)), 0f)
            ]);

        ClimbRouteGeometrySummary geometry = ClimbRouteGeometry.Analyze(
            geometryStages,
            ClimbWorld.Gravity,
            MapNormal(WallNormal(initialPelvisX, initialPelvisUp, wallSlope)));
        return new ZigZagRouteCorpus(holds, initialState, moves, geometry);
    }

    /// <summary>Demo viewer space (x lateral, y up, z toward the climber) -> MapaTur X-east/Y-north/Z-up.</summary>
    private static Vector3 MapPoint(Vector3 demo) => new(demo.X, -demo.Z, demo.Y);

    private static Vector3 MapNormal(Vector3 demo) => Vector3.Normalize(MapPoint(demo));

    private static ClimbHold CreateHold(string id, HoldPlacement placement, float wallSlope)
    {
        // Same micro-relief and slope as the PoC wall, authored in demo axes and rotated afterwards.
        Vector3 demoPosition = new(placement.X, placement.Y, WallDepth(placement.X, placement.Y, wallSlope));
        return new ClimbHold(id, MapPoint(demoPosition), MapNormal(WallNormal(placement.X, placement.Y, wallSlope)), placement.Quality, placement.Type);
    }

    private static float WallDepth(float x, float y, float wallSlope) => RockDepth(x, y) + (wallSlope * (y - 2.15f));

    private static Vector3 WallNormal(float x, float y, float wallSlope)
    {
        if (wallSlope == 0f)
        {
            return Vector3.UnitZ;
        }

        float phaseA = (x * 2.1f) + (y * 0.45f);
        float phaseB = (x * 4.3f) - (y * 0.8f);
        float depthDerivativeX = (0.12f * 2.1f * MathF.Cos(phaseA)) - (0.05f * 4.3f * MathF.Sin(phaseB));
        float depthDerivativeY = (0.12f * 0.45f * MathF.Cos(phaseA)) + (0.05f * 0.8f * MathF.Sin(phaseB)) + wallSlope;
        return Vector3.Normalize(new Vector3(-depthDerivativeX, -depthDerivativeY, 1f));
    }

    private static float RockDepth(float x, float y) =>
        (MathF.Sin((x * 2.1f) + (y * 0.45f)) * 0.12f) +
        (MathF.Cos((x * 4.3f) - (y * 0.8f)) * 0.05f);

    private static RouteStage Stage(
        float centerX,
        float rise,
        float leftHandX,
        float leftHandY,
        float rightHandX,
        float rightHandY,
        float leftFootX,
        float leftFootY,
        float rightFootX,
        float rightFootY,
        bool movesRight,
        int typeIndex)
    {
        ClimbHoldType[] leftTypes =
        [
            ClimbHoldType.Jug, ClimbHoldType.Crimp, ClimbHoldType.Sloper, ClimbHoldType.Pocket,
            ClimbHoldType.Pinch, ClimbHoldType.Crimp, ClimbHoldType.Sloper, ClimbHoldType.Jug
        ];
        ClimbHoldType[] rightTypes =
        [
            ClimbHoldType.Jug, ClimbHoldType.Pinch, ClimbHoldType.Sloper, ClimbHoldType.Crimp,
            ClimbHoldType.Pocket, ClimbHoldType.Pinch, ClimbHoldType.Crimp, ClimbHoldType.Jug
        ];
        return new RouteStage(
            new HoldPlacement(centerX + leftHandX, 3.00f + rise + leftHandY, 0.92f, leftTypes[typeIndex % leftTypes.Length]),
            new HoldPlacement(centerX + rightHandX, 3.00f + rise + rightHandY, 0.80f, rightTypes[typeIndex % rightTypes.Length]),
            new HoldPlacement(centerX + leftFootX, 1.54f + rise + leftFootY, 0.88f, ClimbHoldType.FootEdge),
            new HoldPlacement(centerX + rightFootX, 1.54f + rise + rightFootY, 0.82f, ClimbHoldType.FootEdge),
            movesRight);
    }

    private static string PlannedHoldId(ClimbLimb limb, int level) => limb switch
    {
        ClimbLimb.LeftHand => HoldId("uppera", level),
        ClimbLimb.RightHand => HoldId("upperb", level),
        ClimbLimb.LeftFoot => HoldId("lowera", level),
        ClimbLimb.RightFoot => HoldId("lowerb", level),
        _ => throw new ArgumentOutOfRangeException(nameof(limb), limb, null)
    };

    private static string HoldId(string slot, int level) => $"hold-{level}-{slot}";

    private readonly record struct HoldPlacement(float X, float Y, float Quality, ClimbHoldType Type);

    private readonly record struct RouteStage(
        HoldPlacement LeftHand,
        HoldPlacement RightHand,
        HoldPlacement LeftFoot,
        HoldPlacement RightFoot,
        bool MovesRight);
}

internal sealed record CorpusMove(ClimbLimb Limb, string HoldId);