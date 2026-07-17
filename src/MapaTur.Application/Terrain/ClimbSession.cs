using System.Numerics;

using MapaTur.Climbing;

namespace MapaTur.Application.Terrain;

/// <summary>Top-level owner of body movement. Exactly one system drives position at a time:
/// <see cref="WalkPhysics"/> for Walking/Airborne/Sliding/Roped, <see cref="ClimbSession"/> for Climbing.</summary>
public enum LocomotionMode
{
    Walking,
    Airborne,
    Sliding,
    Climbing,
    Roped,
    Mantling
}

/// <summary>How a grip-climbing session ended.</summary>
public enum ClimbSessionExit
{
    /// <summary>Grip ran out but the auto-belay rope caught the fall — hand back to WalkPhysics roped.</summary>
    RopeCatch,

    /// <summary>Grip ran out with no usable protection — hand back airborne.</summary>
    Fall,

    /// <summary>The climber let go deliberately (jump/topout) — hand back at the current position.</summary>
    Released
}

/// <summary>
/// Rig-derived metrics + tuning for a session. Reach values MUST come from the actual rig
/// (<see cref="ClimberRigKinematics"/> measures them) — the stylized hiker's limbs are far shorter
/// than human anthropometry suggests. Auto-belay numbers come from the same <see cref="WalkParameters"/>
/// that drive walk-mode protection, so both systems agree on spacing/rope/grip rates.
/// </summary>
public sealed record ClimbSessionOptions(
    WalkParameters WalkParameters,
    float ArmReachMeters,
    float LegReachMeters,
    Vector3 LeftShoulderOffsetMeters,
    Vector3 LeftHipOffsetMeters,
    float PelvisHeightMeters,
    Vector3 Gravity)
{
    /// <summary>Grip seconds carried over from walk mode; NaN = start with a full reserve.</summary>
    public float InitialGripStaminaSeconds { get; init; } = float.NaN;

    /// <summary>False = grip does not drain while climbing (no exhaustion rope-catch); tuning decision.</summary>
    public bool DrainGripStamina { get; init; } = true;
}

/// <summary>
/// A hold-by-hold climbing session — the Climbing owner of body position. Wraps the ported
/// <see cref="ClimbSolver"/> (occupancy, reach, pelvis transfer, stability, fatigue) with a
/// direction-intent move picker and the auto-belay (anchor piton on start, a piton every
/// <see cref="WalkParameters.PitonSpacingMeters"/> climbed, rolling cap, rope catch on grip exhaustion).
/// All positions are climb space: X-east/Y-north/Z-up REAL metres.
/// </summary>
public sealed class ClimbSession
{
    private const float CandidateSearchStepMeters = 0.40f;
    private const float CandidateSearchRadiusMeters = 0.60f;
    private const float MinimumProgressMeters = 0.05f;

    private readonly IClimbSurface surface;
    private readonly ClimbSessionOptions options;
    private readonly ClimbSolver solver;
    private readonly List<WalkPhysics.PitonPoint> pitons = [];
    private readonly Dictionary<ClimbLimb, string> plannerCameFrom = []; // anti ping-pong (planner only)
    private float climbDistanceSincePiton;
    private float ropeHangFeetElevation = float.NaN;

    private ClimbSession(IClimbSurface surface, ClimbSessionOptions options, ClimbState initialState)
    {
        this.surface = surface;
        this.options = options;
        solver = new ClimbSolver(
            new ClimbConfiguration(
                HandReach: options.LeftShoulderOffsetMeters.Z + options.ArmReachMeters,
                FootReach: MathF.Abs(options.LeftHipOffsetMeters.Z) + options.LegReachMeters + 0.15f),
            new ClimbMechanicsConfiguration { Gravity = options.Gravity });
        State = initialState;
        GripStamina = float.IsNaN(options.InitialGripStaminaSeconds)
            ? options.WalkParameters.MaxGripStaminaSeconds
            : Math.Clamp(options.InitialGripStaminaSeconds, 0f, options.WalkParameters.MaxGripStaminaSeconds);
        pitons.Add(new WalkPhysics.PitonPoint(PositionXY, FeetElevation));
    }

    public ClimbState State { get; private set; }

    public IReadOnlyList<WalkPhysics.PitonPoint> Pitons => pitons;

    public float GripStamina { get; private set; }

    public float GripStaminaFraction =>
        Math.Clamp(GripStamina / options.WalkParameters.MaxGripStaminaSeconds, 0f, 1f);

    public bool IsFinished { get; private set; }

    public ClimbSessionExit? Exit { get; private set; }

    public string? LastBlockReason { get; private set; }

    /// <summary>Limb and hold of the most recently APPLIED move (for structured logging).</summary>
    public ClimbLimb? LastAppliedLimb { get; private set; }

    public string? LastAppliedHoldId { get; private set; }

    public Vector2 PositionXY => new(State.Pelvis.X, State.Pelvis.Y);

    /// <summary>Real feet elevation for the WalkPhysics handoff: hanging on the rope after a catch,
    /// otherwise the lower planted foot.</summary>
    public float FeetElevation => !float.IsNaN(ropeHangFeetElevation)
        ? ropeHangFeetElevation
        : MathF.Min(
            State.Contacts[ClimbLimb.LeftFoot].Hold.Position.Z,
            State.Contacts[ClimbLimb.RightFoot].Hold.Position.Z);

    /// <summary>
    /// Starts a session at a wall: grabs the four closest suitable holds around rig-derived ideal spots
    /// (hands above the shoulders, feet under the hips). Null when the wall has no usable hold set —
    /// the caller stays in walk mode.
    /// </summary>
    public static ClimbSession? TryStart(IClimbSurface surface, Vector3 pelvisClimb, ClimbSessionOptions options)
        => TryStart(surface, pelvisClimb, options, out _);

    public static ClimbSession? TryStart(
        IClimbSurface surface, Vector3 pelvisClimb, ClimbSessionOptions options, out string? failReason)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(options);
        failReason = null;

        ClimbSurfaceFrame frame = surface.SampleSurface(pelvisClimb, options.Gravity);
        Vector3 basePoint = surface.ProjectToSurface(pelvisClimb);
        Vector3 up = frame.UpAlongSurface;

        // Left/right must be the CHARACTER's left/right, not the surface frame's side axis — its sign is
        // arbitrary relative to the climber and a flipped sign started every session with crossed limbs.
        var facing = new Vector3(-frame.Normal.X, -frame.Normal.Y, 0f);
        facing = facing.LengthSquared() > 1e-6f ? Vector3.Normalize(facing) : new Vector3(0f, -1f, 0f);
        Vector3 characterLeft = Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, facing));

        float shoulderUp = options.LeftShoulderOffsetMeters.Z;
        float hipUp = options.LeftHipOffsetMeters.Z;
        var ideals = new Dictionary<ClimbLimb, Vector3>
        {
            [ClimbLimb.LeftHand] = basePoint + (up * (shoulderUp + (0.55f * options.ArmReachMeters))) + (characterLeft * 0.28f),
            [ClimbLimb.RightHand] = basePoint + (up * (shoulderUp + (0.55f * options.ArmReachMeters))) - (characterLeft * 0.28f),
            [ClimbLimb.LeftFoot] = basePoint + (up * (hipUp - (0.65f * options.LegReachMeters))) + (characterLeft * 0.20f),
            [ClimbLimb.RightFoot] = basePoint + (up * (hipUp - (0.65f * options.LegReachMeters))) - (characterLeft * 0.20f)
        };

        float searchRadius = shoulderUp + options.ArmReachMeters + options.LegReachMeters + 1f;
        IReadOnlyList<ClimbHold> nearby = surface.FindHolds(pelvisClimb, searchRadius);
        var taken = new HashSet<string>();
        var contacts = new Dictionary<ClimbLimb, LimbContact>();
        foreach ((ClimbLimb limb, Vector3 ideal) in ideals)
        {
            ClimbHold? best = null;
            float bestDistance = float.MaxValue;
            foreach (ClimbHold hold in nearby)
            {
                if (taken.Contains(hold.Id) || !hold.SupportsLimb(limb))
                {
                    continue;
                }

                float distance = Vector3.Distance(hold.Position, ideal);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = hold;
                }
            }

            if (best is null || bestDistance > options.ArmReachMeters + options.LegReachMeters)
            {
                failReason = $"no reachable start hold for {limb} (nearest {bestDistance:F1} m from its ideal spot)";
                return null;
            }

            taken.Add(best.Id);
            contacts[limb] = new LimbContact(limb, best, 0f);
        }

        float handTop = (contacts[ClimbLimb.LeftHand].Hold.Position.Z + contacts[ClimbLimb.RightHand].Hold.Position.Z) * 0.5f;
        float footTop = (contacts[ClimbLimb.LeftFoot].Hold.Position.Z + contacts[ClimbLimb.RightFoot].Hold.Position.Z) * 0.5f;
        if (handTop <= footTop + 0.4f)
        {
            failReason = $"terrain too flat to climb here (hand/foot vertical span {handTop - footTop:F2} m < 0.4 m)";
            return null;
        }

        Vector3 pelvis = basePoint + (frame.Normal * 0.52f);
        pelvis.Z = ((handTop + footTop) * 0.5f) - 0.05f;
        return new ClimbSession(surface, options, ClimbState.Create(pelvis, contacts.Values));
    }

    /// <summary>
    /// Manual GRAB for the click flow: occupancy and a generous reach check ONLY — the strict
    /// quasi-static gates (pelvis band, stability, fall risk) stay on the direction planner. A click
    /// within arm's/leg's reach always takes the hold; the pelvis re-centres on the new contact set.
    /// </summary>
    public bool TryGrabHold(ClimbLimb limb, ClimbHold hold)
    {
        ArgumentNullException.ThrowIfNull(hold);
        if (IsFinished)
        {
            return false;
        }

        if (!hold.SupportsLimb(limb))
        {
            LastBlockReason = $"{hold.Id} ({hold.Type}) cannot be used by {limb}.";
            return false;
        }

        ClimbLimb[] occupants = State.Contacts.Values
            .Where(contact => contact.Limb != limb && contact.Hold.Id == hold.Id)
            .Select(contact => contact.Limb)
            .ToArray();
        if (!hold.CanAcceptContact(limb, occupants))
        {
            LastBlockReason = $"{hold.Id} has no free slot for {limb} (occupied by {string.Join(", ", occupants)}).";
            return false;
        }

        float generousReach = GenerousReachMeters(limb);
        float distance = Vector3.Distance(hold.Position, State.Pelvis);
        if (distance > generousReach)
        {
            LastBlockReason = $"{hold.Id} is out of reach for {limb} ({distance:F1} m > {generousReach:F1} m).";
            return false;
        }

        string originHoldId = State.Contacts[limb].Hold.Id;
        var contacts = State.Contacts.ToDictionary(pair => pair.Key, pair => pair.Value);
        contacts[limb] = new LimbContact(
            limb, hold, MathF.Min(1f, State.Contacts[limb].Fatigue + 0.03f));

        // Re-centre the pelvis on the new contact set (clamped between feet and hands when possible).
        Vector3 centroid = Vector3.Zero;
        float handAverage = 0f, footAverage = 0f;
        foreach (LimbContact contact in contacts.Values)
        {
            centroid += contact.Hold.Position;
            if (contact.Limb.IsHand()) { handAverage += contact.Hold.Position.Z * 0.5f; }
            else { footAverage += contact.Hold.Position.Z * 0.5f; }
        }

        centroid /= contacts.Count;
        ClimbSurfaceFrame frame = surface.SampleSurface(centroid, options.Gravity);
        Vector3 pelvis = centroid + (frame.Normal * 0.5f);
        float lower = footAverage + 0.35f;
        float upper = MathF.Max(handAverage - 0.35f, lower);
        pelvis.Z = Math.Clamp((handAverage + footAverage) * 0.5f, lower, upper);

        Vector3 previousPelvis = State.Pelvis;
        State = ClimbState.Create(pelvis, contacts.Values);
        LastAppliedLimb = limb;
        LastAppliedHoldId = hold.Id;
        LastBlockReason = null;
        plannerCameFrom[limb] = originHoldId;
        TrackTravelForPitons(Vector3.Distance(State.Pelvis, previousPelvis));
        return true;
    }

    /// <summary>Reach used by the manual grab flow (looser than the planner's solver gates).</summary>
    public float GenerousReachMeters(ClimbLimb limb) => limb.IsHand()
        ? options.LeftShoulderOffsetMeters.Z + options.ArmReachMeters + 0.40f
        : MathF.Abs(options.LeftHipOffsetMeters.Z) + options.LegReachMeters + 0.45f;

    /// <summary>
    /// Pure what-if: how would the STRICT solver judge moving <paramref name="limb"/> onto
    /// <paramref name="hold"/>? Does not mutate the session — used to annotate candidate holds in the UI.
    /// </summary>
    public (bool PlannerOk, float Risk) AssessMove(ClimbLimb limb, ClimbHold hold)
    {
        ArgumentNullException.ThrowIfNull(hold);
        ClimbMoveResult result = solver.TryMove(State, limb, hold);
        return (result.Succeeded, Math.Clamp(result.Assessment.Risk, 0f, 1f));
    }

    /// <summary>Explicitly move one limb onto a hold (mirrors the PoC mouse mode; full solver gates).</summary>
    public bool TryMoveLimb(ClimbLimb limb, ClimbHold hold)
    {
        if (IsFinished)
        {
            return false;
        }

        string originHoldId = State.Contacts[limb].Hold.Id;
        ClimbMoveResult result = solver.TryMove(State, limb, hold);
        if (!result.Succeeded)
        {
            LastBlockReason = result.FailureReason;
            return false;
        }

        LastAppliedLimb = limb;
        LastAppliedHoldId = hold.Id;
        plannerCameFrom[limb] = originHoldId; // the planner must not instantly undo an explicit move
        Apply(result);
        return true;
    }

    /// <summary>
    /// One intent-driven step: <paramref name="intentSurface"/>.X moves along the wall sideways,
    /// .Y up the wall. Candidates come from the hold index around each limb; the cheapest physically
    /// valid move with real progress wins (neutral ownership — any limb may take any reachable hold).
    /// </summary>
    public bool TryMoveToward(Vector2 intentSurface)
    {
        if (IsFinished || intentSurface.LengthSquared() < 1e-6f)
        {
            return false;
        }

        ClimbSurfaceFrame frame = surface.SampleSurface(State.Pelvis, options.Gravity);
        Vector3 direction = Vector3.Normalize(
            (frame.SideAlongSurface * intentSurface.X) + (frame.UpAlongSurface * intentSurface.Y));

        HashSet<string> occupied = State.Contacts.Values.Select(contact => contact.Hold.Id).ToHashSet();
        ClimbMoveResult? best = null;
        ClimbLimb bestLimb = ClimbLimb.LeftHand;
        string? bestHoldId = null;
        string? bestFromHoldId = null;
        float bestScore = float.MinValue;
        string? reason = null;
        foreach ((ClimbLimb limb, LimbContact contact) in State.Contacts)
        {
            Vector3 searchCentre = contact.Hold.Position + (direction * CandidateSearchStepMeters);
            foreach (ClimbHold candidate in surface.FindHolds(searchCentre, CandidateSearchRadiusMeters))
            {
                if (occupied.Contains(candidate.Id) || !candidate.SupportsLimb(limb))
                {
                    continue;
                }

                // Anti ping-pong: never send a limb straight back to the hold it just left — that read
                // as "one foot flapping between two holds" while every other limb was blocked.
                if (plannerCameFrom.TryGetValue(limb, out string? cameFrom) && cameFrom == candidate.Id)
                {
                    continue;
                }

                float progress = Vector3.Dot(candidate.Position - contact.Hold.Position, direction);
                if (progress < MinimumProgressMeters)
                {
                    continue;
                }

                ClimbMoveResult result = solver.TryMove(State, limb, candidate);
                if (!result.Succeeded)
                {
                    reason = result.FailureReason;
                    continue;
                }

                float score = progress - (0.45f * result.Assessment.Risk);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = result;
                    bestLimb = limb;
                    bestHoldId = candidate.Id;
                    bestFromHoldId = contact.Hold.Id;
                }
            }
        }

        if (best is null)
        {
            LastBlockReason = reason ?? "No reachable hold makes progress in that direction.";
            return false;
        }

        LastAppliedLimb = bestLimb;
        LastAppliedHoldId = bestHoldId;
        if (bestFromHoldId is not null)
        {
            plannerCameFrom[bestLimb] = bestFromHoldId;
        }

        Apply(best);
        return true;
    }

    /// <summary>Drains grip while on the wall; at zero the climber peels off — onto the rope when the
    /// auto-belay can catch, otherwise into a free fall (both end the session).</summary>
    public void Update(float dt)
    {
        if (IsFinished || dt <= 0f || !options.DrainGripStamina)
        {
            return;
        }

        GripStamina = MathF.Max(0f, GripStamina - (options.WalkParameters.GripDrainPerSecond * dt));
        if (GripStamina > 0f)
        {
            return;
        }

        float topPiton = pitons.Max(piton => piton.Elevation);
        float ropeHang = topPiton - options.WalkParameters.RopeLengthMeters;
        if (FeetElevation >= ropeHang - 3f)
        {
            // Rope catch: hang a rope-length under the top piton (WalkPhysics lands it if the ground is higher).
            ropeHangFeetElevation = ropeHang;
            State = ClimbState.Create(
                new Vector3(State.Pelvis.X, State.Pelvis.Y, ropeHang + options.PelvisHeightMeters),
                State.Contacts.Values);
            Exit = ClimbSessionExit.RopeCatch;
        }
        else
        {
            Exit = ClimbSessionExit.Fall;
        }

        IsFinished = true;
    }

    /// <summary>Deliberate release (jump off / topout) — ends the session at the current position.</summary>
    public void Release()
    {
        if (!IsFinished)
        {
            Exit = ClimbSessionExit.Released;
            IsFinished = true;
        }
    }

    /// <summary>The explicit ownership handoff: position, protection, and grip go back to WalkPhysics.</summary>
    public void HandBackTo(WalkPhysics walker)
    {
        ArgumentNullException.ThrowIfNull(walker);
        walker.SyncFromClimb(PositionXY, FeetElevation, pitons, GripStamina, roped: Exit == ClimbSessionExit.RopeCatch);
    }

    private void Apply(ClimbMoveResult result)
    {
        float travelled = Vector3.Distance(result.State.Pelvis, State.Pelvis);
        State = result.State;
        LastBlockReason = null;
        TrackTravelForPitons(travelled);
    }

    private void TrackTravelForPitons(float metersTravelled)
    {
        climbDistanceSincePiton += metersTravelled;
        if (climbDistanceSincePiton >= options.WalkParameters.PitonSpacingMeters)
        {
            climbDistanceSincePiton = 0f;
            pitons.Add(new WalkPhysics.PitonPoint(PositionXY, FeetElevation));
            if (pitons.Count > options.WalkParameters.MaxPitons)
            {
                pitons.RemoveAt(0);
            }
        }
    }
}