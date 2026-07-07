using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Pure first-person walker physics for the terrain camera's "walk mode": gravity, jumping, ground-clamping, and
/// a slope gate. Everything is in REAL metres — <see cref="PositionXY"/> is world east/north metres (the terrain
/// world frame's horizontal plane, which is already unexaggerated) and <see cref="FeetElevation"/> is real
/// elevation metres. The caller multiplies the eye elevation by the vertical-exaggeration ONLY when placing the
/// camera, so slope/gravity/jump all reason about the true mountain, not the stretched render.
///
/// The ground is an injected sampler (world XY → real elevation metres, or null off-coverage), so this whole
/// class is deterministic and unit-testable with no DEM, GL, or camera. Movement rules:
/// <list type="bullet">
/// <item>WALK: you move at the requested speed unless the step's uphill grade (along the move direction) exceeds
/// <see cref="WalkParameters.MaxWalkSlopeGrade"/> — then that step is blocked. Moving across or down a slope is
/// always allowed, so a path that crosses a steep face at a gentle angle (a switchback) stays walkable while
/// climbing the bare face head-on does not.</item>
/// <item>SLIDE: standing on ground whose fall-line grade exceeds <see cref="WalkParameters.MaxStandSlopeGrade"/>,
/// the walker cannot hold and slides downhill — gravity on slopes making high faces effectively inaccessible.</item>
/// <item>JUMP: a ballistic hop that ignores the walk gate, so it can carry the walker onto a ledge too steep to
/// walk up.</item>
/// </list>
/// Not thread-safe: step it from one place (the walk tick), one update at a time.
/// </summary>
public sealed class WalkPhysics
{
    private readonly Func<Vector2, float?> sampleGround;
    private readonly WalkParameters p;
    private int jumpsUsed;

    /// <summary>Creates a walker at <paramref name="startXY"/> (world east/north metres), grounded on the terrain
    /// there. <paramref name="sampleGround"/> maps a world-XY point to its real elevation in metres (null when
    /// outside coverage). <paramref name="parameters"/> tunes feel; null uses <see cref="WalkParameters"/> defaults.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="sampleGround"/> is null.</exception>
    public WalkPhysics(Vector2 startXY, Func<Vector2, float?> sampleGround, WalkParameters? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(sampleGround);
        this.sampleGround = sampleGround;
        this.p = parameters ?? new WalkParameters();
        Teleport(startXY);
    }

    /// <summary>World horizontal position (east, north) in metres.</summary>
    public Vector2 PositionXY { get; private set; }

    /// <summary>Real elevation of the walker's feet in metres (on the ground when <see cref="IsGrounded"/>,
    /// otherwise the current airborne height).</summary>
    public float FeetElevation { get; private set; }

    /// <summary>Vertical velocity in m/s (+up); 0 while grounded.</summary>
    public float VerticalVelocity { get; private set; }

    /// <summary>True when the feet rest on the terrain (not mid-jump/fall).</summary>
    public bool IsGrounded { get; private set; }

    /// <summary>True while sliding down ground too steep to stand on.</summary>
    public bool IsSliding { get; private set; }

    /// <summary>True while the ciupaga is planted into steep rock arresting a fall (hang).</summary>
    public bool IsHanging { get; private set; }

    /// <summary>Real elevation of the camera eye in metres (feet + <see cref="WalkParameters.EyeHeightMeters"/>).
    /// The caller multiplies by the vertical-exaggeration to get the world-Z for the camera.</summary>
    public float EyeElevation => FeetElevation + p.EyeHeightMeters;

    /// <summary>Drops the walker at <paramref name="positionXY"/> and re-grounds it on the terrain there (feet on
    /// the ground, no vertical velocity). Used when entering walk mode or when the camera is repositioned.</summary>
    public void Teleport(Vector2 positionXY)
    {
        PositionXY = positionXY;
        FeetElevation = this.sampleGround(positionXY) ?? 0f;
        VerticalVelocity = 0f;
        IsGrounded = true;
        IsSliding = false;
        IsHanging = false;
        this.jumpsUsed = 0;
    }

    /// <summary>
    /// Advances the walker by <paramref name="dt"/> seconds. <paramref name="wishDirXY"/> is the desired horizontal
    /// move direction in world XY (magnitude ignored; zero = stand still); <paramref name="moveSpeed"/> is the walk
    /// speed in m/s (the caller scales it for running). <paramref name="jumpRequested"/> launches from the ground,
    /// from a hang, or as a mid-air double jump (up to <see cref="WalkParameters.MaxJumps"/> before landing). While
    /// <paramref name="hangHeld"/> is set and the walker is airborne against steep rock within reach, the ciupaga
    /// bites and the fall is arrested (hang) until it's released — the ice-axe self-arrest.
    /// </summary>
    public void Step(float dt, Vector2 wishDirXY, float moveSpeed, bool jumpRequested, bool hangHeld = false)
    {
        if (dt <= 0f)
        {
            return;
        }

        // No ground under the walker (off-coverage, tiles not streamed yet) → hold position rather than fall
        // through the world or crash. The walker resumes when the terrain sample returns.
        if (this.sampleGround(PositionXY) is not float groundHere)
        {
            return;
        }

        Vector2 gradient = Gradient(PositionXY);
        float slope = gradient.Length();

        // Ciupaga self-arrest: airborne, holding the axe against steep rock within reach → hang frozen (no gravity,
        // no drift) until released. A jump this tick overrides it (kick off the wall). Planting re-arms the jumps.
        if (hangHeld && !IsGrounded && !jumpRequested
            && (FeetElevation - groundHere) <= this.p.HangReachMeters
            && slope >= this.p.HangMinSlopeGrade)
        {
            IsHanging = true;
            VerticalVelocity = 0f;
            this.jumpsUsed = 0;
            return;
        }

        IsHanging = false;

        // Jump: from the ground, from a hang, or a mid-air double jump — up to MaxJumps before touching down again.
        if (jumpRequested && this.jumpsUsed < this.p.MaxJumps)
        {
            VerticalVelocity = this.p.JumpSpeedMetersPerSecond;
            IsGrounded = false;
            IsSliding = false;
            this.jumpsUsed++;
        }

        Vector2 move = wishDirXY.LengthSquared() > 1e-8f
            ? Vector2.Normalize(wishDirXY) * (moveSpeed * dt)
            : Vector2.Zero;

        if (!IsGrounded)
        {
            StepAirborne(dt, move);
            return;
        }

        // Grounded: glue the feet to the ground under us, re-arm the jumps, then decide slide vs walk.
        FeetElevation = groundHere;
        this.jumpsUsed = 0;

        if (slope > this.p.MaxStandSlopeGrade)
        {
            SlideDownhill(dt, gradient);
            return;
        }

        IsSliding = false;
        StepWalk(move, groundHere);
    }

    // Airborne: integrate gravity (semi-implicit Euler), allow full horizontal control, and land when the feet
    // descend onto the ground below. Landing snaps the feet exactly to the ground and zeroes vertical velocity.
    private void StepAirborne(float dt, Vector2 move)
    {
        VerticalVelocity -= this.p.GravityMetersPerSecondSquared * dt;
        FeetElevation += VerticalVelocity * dt;

        if (move != Vector2.Zero && this.sampleGround(PositionXY + move) is not null)
        {
            PositionXY += move; // only step onto ground we can sample (never off the mapped world)
        }

        if (VerticalVelocity <= 0f && this.sampleGround(PositionXY) is float ground && FeetElevation <= ground)
        {
            FeetElevation = ground;
            VerticalVelocity = 0f;
            IsGrounded = true;
            this.jumpsUsed = 0; // landed — re-arm jumps
        }
    }

    // Too steep to stand: slide straight down the fall line, ignoring the walk input (gravity wins). Stays
    // grounded (sliding, not falling) and keeps sampling the ground so the descent follows the surface.
    private void SlideDownhill(float dt, Vector2 gradient)
    {
        IsSliding = true;
        Vector2 downhill = -Vector2.Normalize(gradient); // gradient points uphill; negate for the fall line
        Vector2 target = PositionXY + (downhill * (this.p.SlideSpeedMetersPerSecond * dt));
        if (this.sampleGround(target) is float slid)
        {
            PositionXY = target;
            FeetElevation = slid;
        }
    }

    // Walk one step, gated by the along-move uphill grade: an uphill step steeper than MaxWalkSlopeGrade is
    // refused (you cannot climb the bare face — jump it, or take it at an angle). Level/downhill always moves.
    private void StepWalk(Vector2 move, float groundHere)
    {
        if (move == Vector2.Zero)
        {
            return;
        }

        Vector2 target = PositionXY + move;
        if (this.sampleGround(target) is not float groundThere)
        {
            return; // stepping off coverage — hold
        }

        float grade = (groundThere - groundHere) / move.Length();
        bool tooSteepUphill = groundThere > groundHere && grade > this.p.MaxWalkSlopeGrade;
        if (!tooSteepUphill)
        {
            PositionXY = target;
            FeetElevation = groundThere;
        }
    }

    // Central-difference terrain gradient (dz/dx, dz/dy) in real metres per metre at a world-XY point. Any
    // off-coverage probe yields a flat (zero) gradient so an edge tile never reads as an infinite cliff.
    private Vector2 Gradient(Vector2 at)
    {
        float e = this.p.SlopeProbeMeters;
        if (this.sampleGround(at + new Vector2(e, 0f)) is not float ex1
            || this.sampleGround(at - new Vector2(e, 0f)) is not float ex0
            || this.sampleGround(at + new Vector2(0f, e)) is not float ey1
            || this.sampleGround(at - new Vector2(0f, e)) is not float ey0)
        {
            return Vector2.Zero;
        }

        return new Vector2((ex1 - ex0) / (2f * e), (ey1 - ey0) / (2f * e));
    }
}