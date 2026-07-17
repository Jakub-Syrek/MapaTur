using System.Diagnostics;
using System.Numerics;

using MapaTur.Application.Terrain;
using MapaTur.Climbing;

namespace MapaTur.App.Services;

/// <summary>
/// Runtime glue for hold-by-hold climbing in the app: builds a climbable triangle patch from the terrain
/// in front of the walker (real metres — the MVP "steep DEM + procedural micro-holds" level), owns the
/// <see cref="ClimbSession"/> and poses the taken-over realistic climber through
/// <see cref="RealisticClimberRig"/> (the PROVEN Climber3d rig). The posed/skinned buffers are copied into
/// a render-side <see cref="SkinnedModel"/> twin of the same GLB, so the existing renderer path draws the
/// climber unchanged. Climbing requires the licensed model (local data, never redistributed); without it
/// the C key does nothing except log why.
/// </summary>
internal sealed class GripClimbController
{
    private const float PatchHalfWidthMeters = 6f;
    private const float PatchForwardMeters = 10f;
    private const float PatchBackMeters = 2f;
    private const float GridStepMeters = 0.5f;
    private const float MinForwardSlopeGrade = 0.75f;    // ~37 deg — flatter than this is walking, not climbing
    private const float HoldDensityPerVertex = 0.62f;    // ~2.5 holds/m2 — dense enough to always have options
    private const float MoveRepeatSeconds = 0.45f;
    private const float ClimberHeightMeters = 1.85f;

    private static readonly object ClimberModelGate = new();
    private static Task<ClimberAssets?>? climberModelLoad;

    private ClimbSession? session;
    private TrianglePatchClimbSurface? surface;
    private RealisticClimberRig? rig;
    private SkinnedModel? renderModel;
    private SequentialWholeBodyClimbSolver? solver;
    private ClimbWholeBodyRootPose displayedRoot;
    private bool firstSolveDone;
    private bool poseDirty;
    private float moveCooldown;
    private float blockLogCooldown;

    private sealed record ClimberAssets(ClimberSkinnedModel PoseModel, SkinnedModel RenderModel);

    public bool IsActive => session is not null;

    /// <summary>The render-side model the active session poses; the renderer must draw THIS while climbing.</summary>
    public SkinnedModel? ActiveModel { get; private set; }

    public Matrix4x4 HumanoidWorldMatrix { get; private set; } = Matrix4x4.Identity;

    public Matrix4x4 HumanoidRotationMatrix { get; private set; } = Matrix4x4.Identity;

    public bool HasPose { get; private set; }

    /// <summary>
    /// Kicks off the one-time async load of the licensed realistic climber (pose rig + its render twin).
    /// Call when walk mode starts so the model is ready before the first wall.
    /// </summary>
    public static void PreloadClimberModel()
    {
        lock (ClimberModelGate)
        {
            climberModelLoad ??= Task.Run(static () =>
            {
                // AppDataDirectory already ENDS with the \Data segment on Windows — do not append another.
                string path = Environment.GetEnvironmentVariable("MAPATUR_CLIMBER_MODEL")
                    ?? Path.Combine(
                        Microsoft.Maui.Storage.FileSystem.AppDataDirectory, "models", "RockClimber_Realistic.glb");
                try
                {
                    if (!File.Exists(path))
                    {
                        Serilog.Log.Warning("[Climb] climber model missing at {Path} — climbing is unavailable", path);
                        return (ClimberAssets?)null;
                    }

                    var stopwatch = Stopwatch.StartNew();
                    var poseModel = ClimberSkinnedModel.Load(path);
                    var renderTwin = SkinnedModel.Load(path);
                    Serilog.Log.Information(
                        "[Climb] climber model loaded in {Ms} ms: {Verts} verts, {Bones} bones, contactIk={Ik} ({Path})",
                        stopwatch.ElapsedMilliseconds,
                        poseModel.VertexCount,
                        poseModel.BoneCount,
                        poseModel.SupportsContactIk,
                        path);
                    return poseModel.SupportsContactIk ? new ClimberAssets(poseModel, renderTwin) : null;
                }
                catch (Exception ex)
                {
                    Serilog.Log.Warning(ex, "[Climb] climber model failed to load — climbing is unavailable");
                    return null;
                }
            });
        }
    }

    /// <summary>
    /// Tries to start climbing at the walker's position: probes the slope ahead, tessellates it into a
    /// patch with procedurally scattered holds, and grabs the wall with the realistic climber.
    /// False = no climbable rock here or the climber model is not available.
    /// </summary>
    public bool TryEnter(WalkPhysics walker, Func<Vector2, float?> sampleGround, float headingRadians)
    {
        if (IsActive)
        {
            return true;
        }

        if (climberModelLoad is not { IsCompletedSuccessfully: true, Result: { } assets })
        {
            Serilog.Log.Information("[Climb] climber model not ready/available — cannot start a session");
            return false;
        }

        var forward = new Vector2(MathF.Cos(headingRadians), MathF.Sin(headingRadians));
        Vector2 origin = walker.PositionXY;

        // The wall must actually rise ahead of us.
        float here = sampleGround(origin) ?? 0f;
        float ahead = sampleGround(origin + (forward * 2f)) ?? here;
        if ((ahead - here) / 2f < MinForwardSlopeGrade)
        {
            return false;
        }

        TrianglePatchClimbSurface wall = BuildTerrainPatch(origin, forward, sampleGround);
        var rigAdapter = new RealisticClimberRig(assets.PoseModel, ClimberHeightMeters);
        Serilog.Log.Information(
            "[Climb] rig = RockClimber_Realistic, armReach={Arm:F2} m, legReach={Leg:F2} m, pelvisHeight={Pelvis:F2} m",
            rigAdapter.ArmReachMeters, rigAdapter.LegReachMeters, rigAdapter.PelvisHeightMeters);
        var options = new ClimbSessionOptions(
            new WalkParameters(),
            rigAdapter.ArmReachMeters,
            rigAdapter.LegReachMeters,
            rigAdapter.LeftShoulderOffsetMeters,
            rigAdapter.LeftHipOffsetMeters,
            rigAdapter.PelvisHeightMeters,
            ClimbWorld.Gravity)
        {
            InitialGripStaminaSeconds = walker.GripStamina,
            DrainGripStamina = false, // tuning (user): no energy metering while climbing for now
        };
        var pelvis = new Vector3(origin.X, origin.Y, walker.FeetElevation + rigAdapter.PelvisHeightMeters);
        ClimbSession? started = ClimbSession.TryStart(wall, pelvis, options, out string? failReason);
        if (started is null)
        {
            Serilog.Log.Information("[Climb] climb.start_refused: {Reason}", failReason);
            return false;
        }

        session = started;
        surface = wall;
        rig = rigAdapter;
        renderModel = assets.RenderModel;
        ActiveModel = assets.RenderModel;
        solver = new SequentialWholeBodyClimbSolver(
            SmplxPosePriorProfile.CreateBootstrap(),
            new ClimbMechanicsConfiguration { Gravity = ClimbWorld.Gravity });
        displayedRoot = new ClimbWholeBodyRootPose(started.State.Pelvis, YawFacingWall(started.State.Pelvis), 0f, 0f);
        firstSolveDone = false;
        poseDirty = true;
        moveCooldown = 0f;
        HasPose = false;
        MirrorInto(walker);
        return true;
    }

    /// <summary>
    /// One walk-tick of climbing: applies the movement intent (X = sideways along the wall, Y = up),
    /// drains grip, poses the climber after contact changes, and finishes the session on release or
    /// grip exhaustion (rope catch / fall). While active the session is the only owner of the body;
    /// the walker is a read-only mirror for the camera, HUD and rope markers.
    /// </summary>
    public void Tick(float dt, Vector2 intent, bool releaseRequested, float exaggeration, WalkPhysics walker)
    {
        if (session is null || rig is null || surface is null)
        {
            return;
        }

        moveCooldown = MathF.Max(0f, moveCooldown - dt);
        if (intent.LengthSquared() > 1e-4f && moveCooldown <= 0f)
        {
            if (session.TryMoveToward(intent))
            {
                moveCooldown = MoveRepeatSeconds;
                poseDirty = true;
                ClearSelection(); // candidate stats are stale after any move
                Serilog.Log.Information(
                    "[Climb] climb.move_applied {Limb} -> {Hold} pelvisZ={Z:F1} grip={Grip:F0}s",
                    session.LastAppliedLimb, session.LastAppliedHoldId, session.State.Pelvis.Z, session.GripStamina);
            }
            else
            {
                blockLogCooldown -= dt;
                if (blockLogCooldown <= 0f)
                {
                    blockLogCooldown = 1.5f;
                    Serilog.Log.Information(
                        "[Climb] climb.move_blocked intent=({X:F0},{Y:F0}) reason={Reason}",
                        intent.X, intent.Y, session.LastBlockReason);
                }
            }
        }

        session.Update(dt);
        if (releaseRequested)
        {
            session.Release();
        }

        if (session.IsFinished)
        {
            Serilog.Log.Information(
                "[Climb] climb.session_finished exit={Exit} at ({X:F0},{Y:F0}) feet={Feet:F0} grip={Grip:F0}s pitons={Pitons}",
                session.Exit, session.PositionXY.X, session.PositionXY.Y,
                session.FeetElevation, session.GripStamina, session.Pitons.Count);
            session.HandBackTo(walker);
            Clear();
            return;
        }

        if (poseDirty)
        {
            SolveAndPose(exaggeration);
            poseDirty = false;
        }

        MirrorInto(walker);
    }

    /// <summary>A hold the selected limb can take, annotated with the strict solver's verdict.</summary>
    public sealed record ClimbCandidateInfo(ClimbHold Hold, float RiskPercent, bool PlannerOk);

    /// <summary>Currently selected limb of the two-click flow (click 1 = a held hold picks its limb).</summary>
    public ClimbLimb? SelectedLimb { get; private set; }

    private readonly List<ClimbCandidateInfo> candidateHolds = [];

    /// <summary>Holds the selected limb can reach right now (for outlines + per-hold stats).</summary>
    public IReadOnlyList<ClimbCandidateInfo> Candidates => candidateHolds;

    /// <summary>Climb-space position of the selected limb's current hold (highlight anchor).</summary>
    public Vector3? SelectedHoldPosition =>
        SelectedLimb is { } limb && session is not null ? session.State.Contacts[limb].Hold.Position : null;

    /// <summary>
    /// The two-click flow: click 1 on a HELD hold selects that limb (its reachable holds get outlined,
    /// each with the solver's risk); click 2 on a candidate moves the limb there. Clicking another held
    /// hold switches the selection; clicking empty rock clears it.
    /// </summary>
    public void HandleClick(Vector3 rayOriginRender, Vector3 rayDirectionRender, float exaggeration)
    {
        if (session is null || surface is null)
        {
            return;
        }

        ClimbHold? hit = PickHold(rayOriginRender, rayDirectionRender, exaggeration);
        if (hit is null)
        {
            ClearSelection();
            return;
        }

        ClimbLimb? owner = session.State.Contacts
            .Where(pair => pair.Value.Hold.Id == hit.Id)
            .Select(pair => (ClimbLimb?)pair.Key)
            .FirstOrDefault();
        if (owner is { } heldLimb)
        {
            SelectLimb(heldLimb);
            return;
        }

        if (SelectedLimb is not { } selected)
        {
            Serilog.Log.Information("[Climb] click ignored — select a limb first (click one of its held holds)");
            return;
        }

        if (session.TryGrabHold(selected, hit))
        {
            poseDirty = true;
            Serilog.Log.Information(
                "[Climb] climb.move_applied (click) {Limb} -> {Hold} pelvisZ={Z:F1}",
                selected, hit.Id, session.State.Pelvis.Z);
            ClearSelection();
        }
        else
        {
            Serilog.Log.Information(
                "[Climb] climb.move_blocked (click) hold={Hold} reason={Reason}", hit.Id, session.LastBlockReason);
        }
    }

    public void ClearSelection()
    {
        SelectedLimb = null;
        candidateHolds.Clear();
    }

    private void SelectLimb(ClimbLimb limb)
    {
        if (session is null || surface is null)
        {
            return;
        }

        SelectedLimb = limb;
        candidateHolds.Clear();
        string currentHoldId = session.State.Contacts[limb].Hold.Id;
        float reach = session.GenerousReachMeters(limb);
        foreach (ClimbHold hold in surface.Patch.Holds)
        {
            if (hold.Id == currentHoldId
                || !hold.SupportsLimb(limb)
                || Vector3.Distance(hold.Position, session.State.Pelvis) > reach)
            {
                continue;
            }

            ClimbLimb[] occupants = session.State.Contacts.Values
                .Where(contact => contact.Limb != limb && contact.Hold.Id == hold.Id)
                .Select(contact => contact.Limb)
                .ToArray();
            if (!hold.CanAcceptContact(limb, occupants))
            {
                continue;
            }

            (bool plannerOk, float risk) = session.AssessMove(limb, hold);
            candidateHolds.Add(new ClimbCandidateInfo(hold, risk * 100f, plannerOk));
        }

        Serilog.Log.Information(
            "[Climb] limb selected: {Limb} — {Count} reachable holds", limb, candidateHolds.Count);
    }

    private ClimbHold? PickHold(Vector3 rayOriginRender, Vector3 rayDirectionRender, float exaggeration)
    {
        if (surface is null)
        {
            return null;
        }

        ClimbHold? best = null;
        float bestMiss = float.MaxValue;
        foreach (ClimbHold hold in surface.Patch.Holds)
        {
            var p = new Vector3(hold.Position.X, hold.Position.Y, hold.Position.Z * exaggeration);
            float t = Vector3.Dot(p - rayOriginRender, rayDirectionRender);
            if (t <= 0f)
            {
                continue;
            }

            float miss = Vector3.Distance(p, rayOriginRender + (rayDirectionRender * t));
            float tolerance = 0.30f + (0.012f * t); // screen-space-ish slack that grows with distance
            if (miss <= tolerance && miss < bestMiss)
            {
                bestMiss = miss;
                best = hold;
            }
        }

        return best;
    }

    /// <summary>Climb holds + the active contacts as debug markers (positions already exaggerated).</summary>
    public void AppendHoldMarkers(List<Terrain3DGlRenderer.DebugMarker> markers, float exaggeration)
    {
        if (session is null || surface is null)
        {
            return;
        }

        HashSet<string> active = session.State.Contacts.Values.Select(contact => contact.Hold.Id).ToHashSet();
        foreach (ClimbHold hold in surface.Patch.Holds)
        {
            var at = new Vector3(hold.Position.X, hold.Position.Y, hold.Position.Z * exaggeration);
            Vector3 colour = active.Contains(hold.Id)
                ? new Vector3(0.2f, 1f, 0.35f)                     // gripped — green
                : hold.Type == ClimbHoldType.FootEdge
                    ? new Vector3(0.85f, 0.75f, 0.2f)              // foot edge — sand
                    : new Vector3(0.35f, 0.65f, 1f);               // hand-capable — blue
            markers.Add(new Terrain3DGlRenderer.DebugMarker(at, colour, active.Contains(hold.Id) ? 0.12f : 0.07f));
        }
    }

    private void SolveAndPose(float exaggeration)
    {
        if (session is null || rig is null || surface is null || solver is null || renderModel is null)
        {
            return;
        }

        rig.SetClimbContext(surface, session.State.Contacts, ClimbWorld.Gravity);
        var seed = new ClimbWholeBodyRootPose(
            session.State.Pelvis, YawFacingWall(session.State.Pelvis), displayedRoot.PitchRadians, displayedRoot.RollRadians);
        ClimbSurfaceFrame frame = surface.SampleSurface(session.State.Pelvis, ClimbWorld.Gravity);
        var request = new ClimbWholeBodySolveRequest
        {
            ReferencePose = seed,
            SeedPose = firstSolveDone ? displayedRoot with { Pelvis = session.State.Pelvis } : seed,
            SurfaceFrame = frame,
            MaximumContactErrorMeters = 0.12f,
            IncludeCoarseSeeds = !firstSolveDone,
            RefinementPasses = firstSolveDone ? 1 : 2,
            MaximumImprovementRounds = 2,
            RootSearchStepScale = 0.6f,
            OptimizeOrientation = !firstSolveDone
        };
        var solveTimer = Stopwatch.StartNew();
        ClimbWholeBodySolveResult result = solver.Solve(request, rig);
        Serilog.Log.Information(
            "[Climb] climb.whole_body_solved {Ms} ms feasible={Feasible} contactErr={Err:F3} m evals={Evals}",
            solveTimer.ElapsedMilliseconds, result.IsFeasible, result.MaximumContactErrorMeters, result.EvaluationCount);
        displayedRoot = result.Pose.RootPose;
        firstSolveDone = true;

        rig.Evaluate(displayedRoot); // restore the WINNING pose in the bones (Solve probes many candidates)
        rig.Skin();
        CopyPosedBuffers(rig.Model, renderModel);

        Matrix4x4 world = rig.BuildWorldMatrix(displayedRoot);
        Vector3 translation = world.Translation;
        world.Translation = new Vector3(translation.X, translation.Y, translation.Z * exaggeration);
        HumanoidWorldMatrix = world;

        Matrix4x4 rotation = rig.BuildWorldMatrix(displayedRoot with { Pelvis = Vector3.Zero });
        rotation.Translation = Vector3.Zero;
        HumanoidRotationMatrix = NormalizeRotation(rotation);
        HasPose = true;
    }

    private static void CopyPosedBuffers(ClimberSkinnedModel from, SkinnedModel to)
    {
        int count = Math.Min(from.Primitives.Count, to.Primitives.Count);
        for (int index = 0; index < count; index++)
        {
            ClimberSkinnedModel.Primitive source = from.Primitives[index];
            SkinnedModel.Primitive destination = to.Primitives[index];
            if (source.PosedPositions.Length != destination.PosedPositions.Length)
            {
                continue; // primitive enumeration should match (same GLB, same loader rules); skip if not
            }

            Array.Copy(source.PosedPositions, destination.PosedPositions, source.PosedPositions.Length);
            Array.Copy(source.PosedNormals, destination.PosedNormals, source.PosedNormals.Length);
        }
    }

    private void MirrorInto(WalkPhysics walker)
    {
        if (session is not null)
        {
            walker.SyncFromClimb(session.PositionXY, session.FeetElevation, session.Pitons, session.GripStamina, roped: false);
        }
    }

    private float YawFacingWall(Vector3 pelvis)
    {
        if (surface is null)
        {
            return 0f;
        }

        ClimbSurfaceFrame frame = surface.SampleSurface(pelvis, ClimbWorld.Gravity);
        var facing = new Vector2(-frame.Normal.X, -frame.Normal.Y);
        if (facing.LengthSquared() < 1e-6f)
        {
            return 0f;
        }

        facing = Vector2.Normalize(facing);
        return MathF.Atan2(facing.X, -facing.Y); // yaw 0 faces -Y; rotate about +Z onto the into-wall direction
    }

    private static TrianglePatchClimbSurface BuildTerrainPatch(
        Vector2 origin,
        Vector2 forward,
        Func<Vector2, float?> sampleGround)
    {
        var right = new Vector2(forward.Y, -forward.X);
        int columns = (int)(2f * PatchHalfWidthMeters / GridStepMeters) + 1;
        int rows = (int)((PatchForwardMeters + PatchBackMeters) / GridStepMeters) + 1;

        var vertices = new Vector3[columns * rows];
        List<ClimbHold> holds = [];
        for (int j = 0; j < rows; j++)
        {
            float v = -PatchBackMeters + (j * GridStepMeters);
            for (int i = 0; i < columns; i++)
            {
                float u = -PatchHalfWidthMeters + (i * GridStepMeters);
                Vector2 xy = origin + (right * u) + (forward * v);
                float z = sampleGround(xy) ?? 0f;
                vertices[(j * columns) + i] = new Vector3(xy.X, xy.Y, z);
            }
        }

        List<int> indices = [];
        for (int j = 0; j < rows - 1; j++)
        {
            for (int i = 0; i < columns - 1; i++)
            {
                int a = (j * columns) + i;
                int b = a + 1;
                int c = a + columns;
                int d = c + 1;
                indices.AddRange([a, b, c, b, d, c]);
            }
        }

        // Procedural micro-holds: deterministic in world position (the same wall gets the same holds after
        // any reload), roughly one hold per square metre, mixed types. This is the placeholder the real
        // generator (slope/curvature/ortho-classification aware) will replace.
        for (int j = 1; j < rows - 1; j++)
        {
            for (int i = 1; i < columns - 1; i++)
            {
                Vector3 p = vertices[(j * columns) + i];
                float hash = Hash(p.X, p.Y);
                if (hash > HoldDensityPerVertex)
                {
                    continue;
                }

                Vector3 du = vertices[(j * columns) + i + 1] - vertices[(j * columns) + i - 1];
                Vector3 dv = vertices[((j + 1) * columns) + i] - vertices[((j - 1) * columns) + i];
                Vector3 normal = Vector3.Cross(du, dv);
                normal = normal.LengthSquared() > 1e-8f ? Vector3.Normalize(normal) : Vector3.UnitZ;
                if (normal.Z < 0f)
                {
                    normal = -normal;
                }

                float typeHash = Hash(p.Y, p.X + 17.17f);
                ClimbHoldType type = typeHash switch
                {
                    < 0.30f => ClimbHoldType.Jug,
                    < 0.55f => ClimbHoldType.Crimp,
                    < 0.72f => ClimbHoldType.Sloper,
                    _ => ClimbHoldType.FootEdge
                };
                float quality = 0.65f + (0.3f * Hash(p.X + 3.3f, p.Y + 7.7f));
                holds.Add(new ClimbHold($"terrain-{p.X:F1}-{p.Y:F1}-{p.Z:F1}", p, normal, quality, type));
            }
        }

        return new TrianglePatchClimbSurface(
            new ClimbSurfacePatch($"terrain-{origin.X:F0}-{origin.Y:F0}", vertices, indices, holds));
    }

    /// <summary>Deterministic 0..1 hash of a world position (no Random: same rock, same holds, every run).</summary>
    private static float Hash(float a, float b)
    {
        float value = MathF.Sin((a * 12.9898f) + (b * 78.233f)) * 43758.5453f;
        return value - MathF.Floor(value);
    }

    private static Matrix4x4 NormalizeRotation(Matrix4x4 m)
    {
        var x = Vector3.Normalize(new Vector3(m.M11, m.M12, m.M13));
        var y = Vector3.Normalize(new Vector3(m.M21, m.M22, m.M23));
        Vector3 z = Vector3.Cross(x, y);
        y = Vector3.Cross(z, x);
        return new Matrix4x4(
            x.X, x.Y, x.Z, 0f,
            y.X, y.Y, y.Z, 0f,
            z.X, z.Y, z.Z, 0f,
            0f, 0f, 0f, 1f);
    }

    private void Clear()
    {
        session = null;
        surface = null;
        rig = null;
        renderModel = null;
        solver = null;
        ActiveModel = null;
        HasPose = false;
        SelectedLimb = null;
        candidateHolds.Clear();
    }
}
